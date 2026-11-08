using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Skizzy.Protocol.Json;

namespace Skizzy.Host.Core.Net
{
    public interface IConnectionHandler
    {
        /// <summary>Upgrade accepted; the connection is ready. Called on the connection's reader thread.</summary>
        void OnOpen(Connection c);
        /// <summary>A complete text or binary message. The buffer is reused after return. Reader thread.</summary>
        void OnMessage(Connection c, ByteBuffer message, byte opcode);
        /// <summary>Connection finished (either side). Called once, from whichever thread noticed.</summary>
        void OnClosed(Connection c, int code, string reason);
    }

    /// <summary>
    /// One WebSocket connection: a reader thread (handshake, frames) and a writer thread (bounded outbound queue,
    /// heartbeat pings, idle timeout). Network threads never touch Unity (02 §2).
    /// </summary>
    public sealed class Connection
    {
        private struct OutFrame
        {
            public ByteBuffer Buf;
            public byte Opcode;
            public bool Pooled;
        }

        private static int _nextId;

        public readonly int Id;
        public readonly long OpenedTicks = Stopwatch.GetTimestamp();
        public HandshakeDecision Handshake;
        public object SessionTag; // owned by the handler (Session)

        private readonly Socket _socket;
        private readonly NetStream _stream;
        private readonly IConnectionHandler _handler;
        private readonly HandshakePolicy _policy;
        private readonly HostLimits _limits;
        private readonly IHostLog _log;
        private readonly object _q = new object();
        private readonly Queue<OutFrame> _out = new Queue<OutFrame>(32);
        private long _queuedBytes;
        private int _closed;           // 0 open, 1 closing
        private int _closeNotified;
        private int _closeCode = WsClose.Normal;
        private string _closeReason;
        private bool _closeFrameQueued;
        private long _lastReceive = Stopwatch.GetTimestamp();
        private Thread _reader, _writer;

        public long BytesIn;
        public long BytesOut;
        public long MessagesIn;
        public long MessagesOut;
        public bool IsOpen => Volatile.Read(ref _closed) == 0;
        public long QueuedBytes => Interlocked.Read(ref _queuedBytes);

        public Connection(Socket socket, IConnectionHandler handler, HandshakePolicy policy, HostLimits limits, IHostLog log)
        {
            Id = Interlocked.Increment(ref _nextId);
            _socket = socket;
            _socket.NoDelay = true;
            _stream = new NetStream(socket);
            _handler = handler;
            _policy = policy;
            _limits = limits;
            _log = log;
        }

        public void Start()
        {
            _reader = new Thread(ReaderMain) { IsBackground = true, Name = "skizzy-rx-" + Id };
            _reader.Start();
        }

        /// <summary>
        /// Queues a text message. Takes ownership of <paramref name="buf"/> (returned to the pool after writing when
        /// <paramref name="pooled"/>). Returns false if the connection is closing. Closes with SLOW_CONSUMER when the
        /// outbound queue exceeds its cap (03 §1).
        /// </summary>
        // skizzy:hot Connection.Send
        public bool Send(ByteBuffer buf, bool pooled = true, byte opcode = WsOpcode.Text)
        {
            bool overflow = false;
            lock (_q)
            {
                if (_closed != 0 || _closeFrameQueued)
                {
                    if (pooled) ByteBufferPool.Return(buf);
                    return false;
                }
                _out.Enqueue(new OutFrame { Buf = buf, Opcode = opcode, Pooled = pooled });
                _queuedBytes += buf.Length;
                if (_queuedBytes > _limits.OutboundQueueBytes) overflow = true;
                Monitor.Pulse(_q);
            }
            if (overflow) Close(WsClose.ForErrorCode(-32024), "SLOW_CONSUMER");
            return true;
        }

        /// <summary>Starts a close: queues a close frame (writer flushes then shuts the socket).</summary>
        public void Close(int code, string reason)
        {
            lock (_q)
            {
                if (_closeFrameQueued || _closed != 0) return;
                _closeFrameQueued = true;
                _closeCode = code;
                _closeReason = reason;
                Monitor.Pulse(_q);
            }
            if (_writer == null) Abort(code, reason);
        }

        /// <summary>Immediate teardown without a close frame.</summary>
        public void Abort(int code, string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _closeCode = code;
                _closeReason = reason;
                try { _socket.Shutdown(SocketShutdown.Both); } catch (Exception) { }
                try { _socket.Close(); } catch (Exception) { }
                lock (_q)
                {
                    while (_out.Count > 0)
                    {
                        var f = _out.Dequeue();
                        if (f.Pooled) ByteBufferPool.Return(f.Buf);
                    }
                    _queuedBytes = 0;
                    Monitor.PulseAll(_q);
                }
            }
            NotifyClosed();
        }

        private void NotifyClosed()
        {
            if (Interlocked.Exchange(ref _closeNotified, 1) != 0) return;
            try { _handler.OnClosed(this, _closeCode, _closeReason); }
            catch (Exception e) { _log.Error("connection close handler failed: " + e.Message); }
        }

        private void ReaderMain()
        {
            var msg = new ByteBuffer(4096);
            try
            {
                if (!DoHandshake()) return;
                _writer = new Thread(WriterMain) { IsBackground = true, Name = "skizzy-tx-" + Id };
                _writer.Start();
                _handler.OnOpen(this);
                var reader = new WsFrameReader(_stream, _limits.MaxMessageBytes);
                byte pending = 0;
                while (Volatile.Read(ref _closed) == 0)
                {
                    long before = reader.BytesRead;
                    byte op = reader.Read(msg, ref pending);
                    Interlocked.Add(ref BytesIn, reader.BytesRead - before);
                    Volatile.Write(ref _lastReceive, Stopwatch.GetTimestamp());
                    switch (op)
                    {
                        case WsOpcode.Text:
                        case WsOpcode.Binary:
                            Interlocked.Increment(ref MessagesIn);
                            _handler.OnMessage(this, msg, op);
                            if (msg.Capacity > 1 << 20) msg = new ByteBuffer(4096); // do not pin a large buffer per idle connection
                            break;
                        case WsOpcode.Ping:
                            SendControl(WsOpcode.Pong, reader.ControlPayload, reader.LastControlLength);
                            break;
                        case WsOpcode.Pong:
                            break;
                        case WsOpcode.Close:
                            int code = WsClose.Normal;
                            if (reader.LastControlLength >= 2) code = (reader.ControlPayload[0] << 8) | reader.ControlPayload[1];
                            Close(WsClose.Normal, "client closed (" + code + ")");
                            return;
                    }
                }
            }
            catch (WsProtocolException e)
            {
                Close(e.CloseCode, e.Message);
            }
            catch (EndOfStreamException)
            {
                Abort(WsClose.GoingAway, "peer closed");
            }
            catch (IOException)
            {
                Abort(WsClose.GoingAway, "connection reset");
            }
            catch (SocketException)
            {
                Abort(WsClose.GoingAway, "connection reset");
            }
            catch (ObjectDisposedException)
            {
                Abort(WsClose.GoingAway, "closed");
            }
            catch (Exception e)
            {
                _log.Error("reader " + Id + " failed: " + e);
                Abort(WsClose.InternalError, "internal error");
            }
        }

        private bool DoHandshake()
        {
            var buf = new byte[WsHandshake.MaxRequestBytes];
            int len = 0, end = -1;
            _socket.ReceiveTimeout = 5000;
            while (end < 0)
            {
                if (len == buf.Length)
                {
                    RefuseAndClose(new HandshakeDecision { Status = 400, Reason = "too_large", Message = "Request header too large." });
                    return false;
                }
                int r;
                try { r = _stream.Read(buf, len, buf.Length - len); }
                catch (IOException) { Abort(WsClose.GoingAway, "handshake timeout"); return false; }
                if (r <= 0) { Abort(WsClose.GoingAway, "closed during handshake"); return false; }
                len += r;
                end = WsHandshake.FindHeaderEnd(buf, len);
            }
            if (end != len)
            {
                // Clients must wait for the 101 before sending frames.
                RefuseAndClose(new HandshakeDecision { Status = 400, Reason = "early_data", Message = "Data sent before the handshake completed." });
                return false;
            }
            var req = WsHandshake.Parse(buf, end);
            var d = WsHandshake.Validate(req, _policy);
            Handshake = d;
            if (!d.Ok)
            {
                _log.Info("refused connection " + Id + ": " + d.Status + " " + d.Reason);
                RefuseAndClose(d);
                return false;
            }
            var resp = WsHandshake.BuildAccept(req, d);
            _stream.Write(resp, 0, resp.Length);
            _socket.ReceiveTimeout = 0;
            return true;
        }

        private void RefuseAndClose(HandshakeDecision d)
        {
            Handshake = d;
            try
            {
                var bytes = WsHandshake.BuildRefusal(d);
                _stream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception) { }
            Abort(WsClose.PolicyViolation, "handshake refused: " + d.Reason);
        }

        private void SendControl(byte opcode, byte[] payload, int len)
        {
            var b = ByteBufferPool.Rent(len + 2);
            b.WriteBytes(payload, 0, len);
            Send(b, true, opcode);
        }

        private void WriterMain()
        {
            var w = new WsFrameWriter();
            long pingTicks = (long)(_limits.PingIntervalS * Stopwatch.Frequency);
            long idleTicks = (long)(_limits.IdleTimeoutS * Stopwatch.Frequency);
            long lastPing = Stopwatch.GetTimestamp();
            var closePayload = new byte[125];
            try
            {
                while (true)
                {
                    OutFrame f;
                    bool haveFrame = false, sendClose = false;
                    lock (_q)
                    {
                        while (_out.Count == 0 && !_closeFrameQueued && _closed == 0)
                        {
                            Monitor.Wait(_q, 5000);
                            long now = Stopwatch.GetTimestamp();
                            if (now - Volatile.Read(ref _lastReceive) > idleTicks)
                            {
                                _closeFrameQueued = true;
                                _closeCode = WsClose.PolicyViolation;
                                _closeReason = "idle timeout";
                                break;
                            }
                            if (now - lastPing > pingTicks) break;
                        }
                        if (_closed != 0) return;
                        if (_out.Count > 0)
                        {
                            f = _out.Dequeue();
                            _queuedBytes -= f.Buf.Length;
                            haveFrame = true;
                        }
                        else
                        {
                            f = default(OutFrame);
                            sendClose = _closeFrameQueued;
                        }
                    }
                    if (haveFrame)
                    {
                        int n = w.Write(_stream, f.Opcode, f.Buf.Data, 0, f.Buf.Length);
                        Interlocked.Add(ref BytesOut, n);
                        if (f.Opcode == WsOpcode.Text || f.Opcode == WsOpcode.Binary) Interlocked.Increment(ref MessagesOut);
                        if (f.Pooled) ByteBufferPool.Return(f.Buf);
                        continue;
                    }
                    if (sendClose)
                    {
                        int n = WsFrameWriter.BuildClosePayload(closePayload, _closeCode, _closeReason);
                        try { w.Write(_stream, WsOpcode.Close, closePayload, 0, n); } catch (Exception) { }
                        Thread.Sleep(50);
                        Abort(_closeCode, _closeReason);
                        return;
                    }
                    long t = Stopwatch.GetTimestamp();
                    if (t - lastPing > pingTicks)
                    {
                        lastPing = t;
                        w.Write(_stream, WsOpcode.Ping, closePayload, 0, 0);
                    }
                }
            }
            catch (Exception)
            {
                Abort(WsClose.GoingAway, "write failed");
            }
        }

        /// <summary>Minimal stream over a socket (NetworkStream with no ownership quirks).</summary>
        private sealed class NetStream : Stream
        {
            private readonly Socket _s;
            public NetStream(Socket s) { _s = s; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                try { return _s.Receive(buffer, offset, count, SocketFlags.None); }
                catch (SocketException e) { throw new IOException(e.Message, e); }
            }
            public override void Write(byte[] buffer, int offset, int count)
            {
                try
                {
                    while (count > 0)
                    {
                        int n = _s.Send(buffer, offset, count, SocketFlags.None);
                        offset += n;
                        count -= n;
                    }
                }
                catch (SocketException e) { throw new IOException(e.Message, e); }
            }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
        }
    }
}
