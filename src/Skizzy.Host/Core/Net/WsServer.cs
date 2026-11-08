using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Skizzy.Host.Core.Net
{
    /// <summary>
    /// Loopback TCP acceptor (02 §2 "Acceptor"). Binds 127.0.0.1 on the first free port of the configured range
    /// (47549-47559, 02 §5), enforces the host-wide connection cap and hands sockets to <see cref="Connection"/>.
    /// The accept call blocks, so an idle host with nothing connected costs no CPU.
    /// </summary>
    public sealed class WsServer
    {
        private readonly IConnectionHandler _handler;
        private readonly HandshakePolicy _policy;
        private readonly HostLimits _limits;
        private readonly IHostLog _log;
        private readonly List<Connection> _conns = new List<Connection>();
        private Socket _listener;
        private Thread _thread;
        private volatile bool _stopping;

        public int Port { get; private set; }
        public long Refused;
        public long Accepted;
        /// <summary>Last refused handshake ("403 origin_denied"), shown in the Connections window.</summary>
        public volatile string LastRefusal;

        public WsServer(IConnectionHandler handler, HandshakePolicy policy, HostLimits limits, IHostLog log)
        {
            _handler = handler;
            _policy = policy;
            _limits = limits;
            _log = log;
        }

        /// <summary>Binds the first free port in [first, last]. Returns the port, or throws if none is free.</summary>
        public int Start(int first, int last)
        {
            SocketException lastError = null;
            for (int port = first; port <= last; port++)
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    s.ExclusiveAddressUse = true;
                    s.Bind(new IPEndPoint(IPAddress.Loopback, port));
                    s.Listen(16);
                    _listener = s;
                    Port = ((IPEndPoint)s.LocalEndPoint).Port; // port 0 (tests) binds an ephemeral port
                    _policy.Port = Port;
                    break;
                }
                catch (SocketException e)
                {
                    lastError = e;
                    try { s.Close(); } catch (Exception) { }
                }
            }
            if (_listener == null)
                throw new InvalidOperationException("no free port in " + first + "-" + last + ": " + (lastError != null ? lastError.Message : "?"));
            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "skizzy-accept" };
            _thread.Start();
            return Port;
        }

        /// <summary>New token for future handshakes (existing sessions are closed by the host).</summary>
        public void SetToken(byte[] token) { _policy.TokenBytes = token; }

        public int ConnectionCount
        {
            get { lock (_conns) return _conns.Count; }
        }

        public void Snapshot(List<Connection> into)
        {
            lock (_conns) into.AddRange(_conns);
        }

        internal void Remove(Connection c)
        {
            lock (_conns) _conns.Remove(c);
        }

        private void AcceptLoop()
        {
            while (!_stopping)
            {
                Socket s;
                try { s = _listener.Accept(); }
                catch (SocketException) { if (_stopping) return; continue; }
                catch (ObjectDisposedException) { return; }
                if (!(s.RemoteEndPoint is IPEndPoint ep) || !IPAddress.IsLoopback(ep.Address))
                {
                    Interlocked.Increment(ref Refused);
                    try { s.Close(); } catch (Exception) { }
                    continue;
                }
                Connection c;
                lock (_conns)
                {
                    if (_conns.Count >= _limits.MaxConnections)
                    {
                        Interlocked.Increment(ref Refused);
                        RefuseBusy(s);
                        continue;
                    }
                    c = new Connection(s, new Tracking(this, _handler), _policy, _limits, _log);
                    _conns.Add(c);
                }
                Interlocked.Increment(ref Accepted);
                c.Start();
            }
        }

        private static void RefuseBusy(Socket s)
        {
            try
            {
                var d = new HandshakeDecision { Status = 503, Reason = "too_many_connections", Message = "Connection limit reached." };
                var b = WsHandshake.BuildRefusal(d);
                s.Send(b);
            }
            catch (Exception) { }
            try { s.Close(); } catch (Exception) { }
        }

        public void Stop()
        {
            _stopping = true;
            try { _listener?.Close(); } catch (Exception) { }
            var copy = new List<Connection>();
            Snapshot(copy);
            foreach (var c in copy) c.Close(WsClose.GoingAway, "host stopping");
            _thread?.Join(1000);
        }

        /// <summary>Wraps the handler so closed connections leave the table.</summary>
        private sealed class Tracking : IConnectionHandler
        {
            private readonly WsServer _srv;
            private readonly IConnectionHandler _inner;
            public Tracking(WsServer srv, IConnectionHandler inner) { _srv = srv; _inner = inner; }
            public void OnOpen(Connection c) { _inner.OnOpen(c); }
            public void OnMessage(Connection c, Skizzy.Protocol.Json.ByteBuffer m, byte op) { _inner.OnMessage(c, m, op); }
            public void OnClosed(Connection c, int code, string reason)
            {
                _srv.Remove(c);
                if (c.Handshake != null && c.Handshake.Ok) _inner.OnClosed(c, code, reason);
                else
                {
                    Interlocked.Increment(ref _srv.Refused);
                    _srv.LastRefusal = c.Handshake != null ? c.Handshake.Status + " " + c.Handshake.Reason : reason;
                }
            }
        }
    }
}
