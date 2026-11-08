using System;
using System.Threading;
using Skizzy.Client;
using Skizzy.Host.Core;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using Xunit;

namespace Skizzy.Host.Tests
{
    /// <summary>Acceptance 6: Origin-bearing, token-less and bad-token connections refused; Host header checked.</summary>
    public class SecurityTests : IDisposable
    {
        private readonly TestHost _t = new TestHost();
        public void Dispose() { _t.Dispose(); }

        private string Auth => "Authorization: Bearer " + _t.Sim.Token + "\r\n";

        [Fact]
        public void Good_handshake_is_accepted_with_subprotocol()
        {
            var r = Raw.Handshake(_t.Host.Port, Auth + "Sec-WebSocket-Protocol: skizzy.v1\r\n");
            Assert.StartsWith("HTTP/1.1 101", r);
            Assert.Contains("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", r); // RFC 6455 §1.3 sample
            Assert.Contains("Sec-WebSocket-Protocol: skizzy.v1", r);
        }

        [Fact]
        public void Origin_header_is_refused()
        {
            var r = Raw.Handshake(_t.Host.Port, Auth + "Origin: https://evil.example\r\n");
            Assert.StartsWith("HTTP/1.1 403", r);
            Assert.Contains("origin_denied", r);
        }

        [Fact]
        public void Missing_token_is_refused()
        {
            var r = Raw.Handshake(_t.Host.Port, "");
            Assert.StartsWith("HTTP/1.1 401", r);
            Assert.Contains("no_token", r);
        }

        [Fact]
        public void Bad_token_is_refused()
        {
            var r = Raw.Handshake(_t.Host.Port, "Authorization: Bearer AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\r\n");
            Assert.StartsWith("HTTP/1.1 401", r);
            Assert.Contains("bad_token", r);
        }

        [Fact]
        public void Foreign_host_header_is_refused_dns_rebinding()
        {
            var r = Raw.Handshake(_t.Host.Port, Auth, host: "evil.example:" + _t.Host.Port);
            Assert.StartsWith("HTTP/1.1 403", r);
            Assert.Contains("host_denied", r);
            var r2 = Raw.Handshake(_t.Host.Port, Auth, host: "localhost:" + _t.Host.Port);
            Assert.StartsWith("HTTP/1.1 101", r2);
        }

        [Fact]
        public void Wrong_path_and_subprotocol_are_refused()
        {
            Assert.StartsWith("HTTP/1.1 404", Raw.Handshake(_t.Host.Port, Auth, path: "/other"));
            Assert.StartsWith("HTTP/1.1 400", Raw.Handshake(_t.Host.Port, Auth + "Sec-WebSocket-Protocol: chat\r\n"));
        }

        [Fact]
        public void Refusals_are_counted()
        {
            Raw.Handshake(_t.Host.Port, "Origin: http://x\r\n");
            Expect.Eventually(() => _t.Host.Server.Refused >= 1, 2000, "refusal counted");
            Assert.Contains("403", _t.Host.Server.LastRefusal);
        }

        [Fact]
        public void Client_with_bad_token_gets_a_clear_error()
        {
            var c = new SkizzyClient();
            var ex = Assert.Throws<SkizzyClientException>(() =>
                c.ConnectAsync(new Endpoint { Url = _t.Sim.Url, Token = "wrong" }, new HelloOptions()).GetAwaiter().GetResult());
            Assert.Equal("CONNECT_FAILED", ex.Code);
            c.Dispose();
        }

        [Fact]
        public void Calls_before_hello_are_rejected()
        {
            // A raw client that skips hello: use the SDK's socket by connecting, then a second request on a fresh socket.
            var ws = new System.Net.WebSockets.ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", "Bearer " + _t.Sim.Token);
            ws.ConnectAsync(new Uri(_t.Sim.Url), CancellationToken.None).Wait();
            var req = System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ksp.vessels.list\",\"params\":{}}");
            ws.SendAsync(new ArraySegment<byte>(req), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None).Wait();
            var buf = new byte[4096];
            var r = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
            var resp = JsonValue.Parse(System.Text.Encoding.UTF8.GetString(buf, 0, r.Count));
            Assert.Equal(-32600, resp.GetObj("error").GetInt("code"));
            Assert.Equal("no_hello", resp.GetObj("error").GetObj("data").GetString("reason"));
            ws.Dispose();
        }

        [Fact]
        public void Batches_and_bad_json_get_protocol_errors()
        {
            var ws = new System.Net.WebSockets.ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", "Bearer " + _t.Sim.Token);
            ws.ConnectAsync(new Uri(_t.Sim.Url), CancellationToken.None).Wait();
            JsonValue Send(string text)
            {
                var b = System.Text.Encoding.UTF8.GetBytes(text);
                ws.SendAsync(new ArraySegment<byte>(b), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None).Wait();
                var buf = new byte[4096];
                var r = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
                return JsonValue.Parse(System.Text.Encoding.UTF8.GetString(buf, 0, r.Count));
            }
            Assert.Equal(-32700, Send("{not json").GetObj("error").GetInt("code"));
            Assert.Equal(-32600, Send("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\"}]").GetObj("error").GetInt("code"));
            ws.Dispose();
        }
    }

    public class OriginAllowListTests : IDisposable
    {
        private readonly TestHost _t = new TestHost(null, new HostSettings { PortFirst = 0, PortLast = 0, AllowedOrigins = new[] { "http://localhost:5173" } });
        public void Dispose() { _t.Dispose(); }

        [Fact]
        public void Allow_listed_origin_without_token_must_send_token_in_hello()
        {
            var r = Raw.Handshake(_t.Host.Port, "Origin: http://localhost:5173\r\n");
            Assert.StartsWith("HTTP/1.1 101", r);

            var ws = new System.Net.WebSockets.ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", "http://localhost:5173");
            ws.ConnectAsync(new Uri(_t.Sim.Url), CancellationToken.None).Wait();
            var hello = System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"session.hello\",\"params\":{\"protocol\":\"skizzy-protocol\",\"versions\":[\"1.0\"]}}");
            ws.SendAsync(new ArraySegment<byte>(hello), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None).Wait();
            var buf = new byte[4096];
            var res = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
            var resp = JsonValue.Parse(System.Text.Encoding.UTF8.GetString(buf, 0, res.Count));
            Assert.Equal("UNAUTHENTICATED", resp.GetObj("error").GetObj("data").GetString("kind"));
            var close = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
            Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Close, close.MessageType);
            Assert.Equal(4001, (int)ws.CloseStatus.Value);
            ws.Dispose();
        }
    }
}
