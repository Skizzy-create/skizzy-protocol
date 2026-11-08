using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Skizzy.Client;
using Skizzy.Host.Core;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using Skizzy.Sim;
using Xunit;

namespace Skizzy.Host.Tests
{
    /// <summary>A FakeKsp host on an ephemeral loopback port in a temp KSP root.</summary>
    public sealed class TestHost : IDisposable
    {
        public readonly SimHost Sim;
        public readonly string Root;

        public TestHost(FakeWorld world = null, HostSettings settings = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "skizzy-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Root);
            Sim = new SimHost(Root, world, settings ?? new HostSettings { PortFirst = 0, PortLast = 0 });
            Sim.Host.GraceS = 0.2;
            Sim.Start();
        }

        public HostCore Host => Sim.Host;
        public FakeWorld World => Sim.World;
        public Endpoint Endpoint => new Endpoint { Url = Sim.Url, Token = Sim.Token };

        public SkizzyClient Connect(HelloOptions hello = null)
        {
            var c = new SkizzyClient();
            c.ConnectAsync(Endpoint, hello ?? new HelloOptions { ClientName = "test", ClientKind = "sdk", ActorKind = "script" }).GetAwaiter().GetResult();
            return c;
        }

        public SkizzyClient ConnectLlm(string role = "commander", string side = "Blue", bool? fog = null)
        {
            return Connect(new HelloOptions { ClientName = "skizzy-bridge", ClientKind = "bridge", ActorKind = "llm", Role = role, Side = side, Fog = fog, ActorId = "a:" + side + "-" + role + "-" + Guid.NewGuid().ToString("N").Substring(0, 4) });
        }

        /// <summary>Runs an action on the FakeKsp main thread and waits for it.</summary>
        public void OnMain(Action a)
        {
            var done = new ManualResetEventSlim();
            Exception err = null;
            Host.Dispatcher.EnqueueMain(new Job { Action = () => { try { a(); } catch (Exception e) { err = e; } finally { done.Set(); } } });
            Assert.True(done.Wait(5000), "main-thread action timed out");
            if (err != null) throw new Exception("main-thread action failed", err);
        }

        public void Dispose()
        {
            Sim.Dispose();
            try { Directory.Delete(Root, true); } catch (Exception) { }
        }
    }

    public static class Raw
    {
        /// <summary>Sends a raw HTTP upgrade and returns the status line and headers.</summary>
        public static string Handshake(int port, string extraHeaders, string path = "/skizzy", string host = null)
        {
            using (var tcp = new TcpClient())
            {
                tcp.Connect("127.0.0.1", port);
                var s = tcp.GetStream();
                s.ReadTimeout = 5000;
                string req = "GET " + path + " HTTP/1.1\r\nHost: " + (host ?? "127.0.0.1:" + port) + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                             "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n" + extraHeaders + "\r\n";
                var b = Encoding.ASCII.GetBytes(req);
                s.Write(b, 0, b.Length);
                var buf = new byte[4096];
                int n = 0;
                try
                {
                    while (n < buf.Length)
                    {
                        int r = s.Read(buf, n, buf.Length - n);
                        if (r <= 0) break;
                        n += r;
                        if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n")) break;
                    }
                }
                catch (IOException) { }
                return Encoding.ASCII.GetString(buf, 0, n);
            }
        }
    }

    public static class Expect
    {
        public static SkizzyError Error(Func<JsonValue> call, ErrorKind kind, string reason = null)
        {
            try
            {
                var r = call();
                Assert.Fail("expected " + kind + " but got " + r);
                return null;
            }
            catch (SkizzyError e)
            {
                Assert.True(e.Kind == kind, "expected " + kind + " got " + e.Kind + ": " + e.Message);
                if (reason != null) Assert.Equal(reason, e.Reason);
                return e;
            }
        }

        public static void Eventually(Func<bool> cond, int timeoutMs = 5000, string what = "condition")
        {
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                if (cond()) return;
                Thread.Sleep(20);
            }
            Assert.Fail("timed out waiting for " + what);
        }
    }
}
