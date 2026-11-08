using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Skizzy.Host.Core.Net
{
    /// <summary>Parsed HTTP upgrade request (RFC 6455 §4.2.1). Header names are lower-cased.</summary>
    public sealed class HandshakeRequest
    {
        public string Method;
        public string Target;
        public string Path;
        public string HttpVersion;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Header(string name)
        {
            return Headers.TryGetValue(name, out var v) ? v : null;
        }
    }

    /// <summary>Outcome of validating an upgrade request against the host's security rules.</summary>
    public sealed class HandshakeDecision
    {
        public int Status;               // 101 on success
        public string Reason;            // short machine reason for logs, e.g. "origin_denied"
        public string Message;           // body text for the refusal
        public string SubProtocol;       // echoed when the client offered skizzy.v1
        public bool TokenVerified;       // Authorization: Bearer matched
        public string Origin;            // allow-listed Origin (token then expected in hello)
        public bool Ok => Status == 101;
    }

    /// <summary>Security inputs for the handshake: the secret token, allowed origins and the bound port.</summary>
    public sealed class HandshakePolicy
    {
        public byte[] TokenBytes;        // ASCII bytes of the token; never logged
        public string[] AllowedOrigins = new string[0];
        public int Port;
        public string Path = Skizzy.Protocol.ProtocolInfo.Path;
    }

    /// <summary>
    /// RFC 6455 opening handshake: parsing, validation (path, Host, Origin, token, version, key, subprotocol)
    /// and response bytes. Rules from 03 §1 and PLAN §6.8: loopback only; any Origin header is refused unless it is
    /// allow-listed (browser pages can open ws://127.0.0.1); Host must name the loopback address and the bound port
    /// (defeats DNS rebinding); the token is compared in constant time.
    /// </summary>
    public static class WsHandshake
    {
        public const int MaxRequestBytes = 8192;
        private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        /// <summary>Index just past "\r\n\r\n", or -1 if the header block is incomplete.</summary>
        public static int FindHeaderEnd(byte[] buf, int len)
        {
            for (int i = 3; i < len; i++)
                if (buf[i] == '\n' && buf[i - 1] == '\r' && buf[i - 2] == '\n' && buf[i - 3] == '\r') return i + 1;
            return -1;
        }

        /// <summary>Parses the request head. Returns null if it is not a well-formed HTTP/1.1 request.</summary>
        public static HandshakeRequest Parse(byte[] buf, int headerLen)
        {
            for (int i = 0; i < headerLen; i++)
                if (buf[i] > 0x7E || (buf[i] < 0x20 && buf[i] != '\r' && buf[i] != '\n' && buf[i] != '\t')) return null;
            string text = Encoding.ASCII.GetString(buf, 0, headerLen);
            var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length < 2) return null;
            var first = lines[0].Split(' ');
            if (first.Length != 3) return null;
            var r = new HandshakeRequest { Method = first[0], Target = first[1], HttpVersion = first[2] };
            int q = r.Target.IndexOf('?');
            r.Path = q >= 0 ? r.Target.Substring(0, q) : r.Target;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                int c = line.IndexOf(':');
                if (c <= 0) return null;
                string name = line.Substring(0, c).Trim();
                string value = line.Substring(c + 1).Trim();
                if (name.Length == 0) return null;
                if (r.Headers.TryGetValue(name, out var existing)) r.Headers[name] = existing + ", " + value;
                else r.Headers[name] = value;
            }
            return r;
        }

        public static HandshakeDecision Validate(HandshakeRequest r, HandshakePolicy p)
        {
            if (r == null) return Refuse(400, "bad_request", "Malformed HTTP request.");
            if (r.Method != "GET" || r.HttpVersion != "HTTP/1.1") return Refuse(400, "bad_request", "Expected GET ... HTTP/1.1.");
            if (!HostAllowed(r.Header("Host"), p.Port)) return Refuse(403, "host_denied", "Host header must be 127.0.0.1, localhost or [::1] with the bound port.");

            string origin = r.Header("Origin");
            string allowedOrigin = null;
            if (origin != null)
            {
                foreach (var o in p.AllowedOrigins)
                    if (string.Equals(o, origin, StringComparison.OrdinalIgnoreCase)) { allowedOrigin = origin; break; }
                if (allowedOrigin == null) return Refuse(403, "origin_denied", "Requests with an Origin header are refused unless the origin is allow-listed in settings.cfg.");
            }

            if (r.Path != p.Path) return Refuse(404, "bad_path", "WebSocket endpoint is " + p.Path + ".");
            if (!TokenListContains(r.Header("Upgrade"), "websocket")) return Refuse(426, "not_upgrade", "This endpoint only speaks WebSocket (Upgrade: websocket).");
            if (!TokenListContains(r.Header("Connection"), "upgrade")) return Refuse(400, "bad_request", "Connection: Upgrade required.");
            if (r.Header("Sec-WebSocket-Version") != "13") return Refuse(426, "bad_version", "Sec-WebSocket-Version must be 13.");
            string key = r.Header("Sec-WebSocket-Key");
            if (!KeyValid(key)) return Refuse(400, "bad_key", "Sec-WebSocket-Key must be 16 random bytes, base64.");

            string sub = null;
            string offered = r.Header("Sec-WebSocket-Protocol");
            if (offered != null)
            {
                if (!TokenListContains(offered, Skizzy.Protocol.ProtocolInfo.SubProtocol))
                    return Refuse(400, "bad_subprotocol", "Subprotocol must be " + Skizzy.Protocol.ProtocolInfo.SubProtocol + ".");
                sub = Skizzy.Protocol.ProtocolInfo.SubProtocol;
            }

            bool tokenOk = false;
            string auth = r.Header("Authorization");
            if (auth != null)
            {
                if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Refuse(401, "bad_token", "Authorization must be 'Bearer <token>'.");
                tokenOk = TokenEquals(auth.Substring(7).Trim(), p.TokenBytes);
                if (!tokenOk) return Refuse(401, "bad_token", "Invalid token. Read it from GameData/SkizzyProtocol/PluginData/auth.token.");
            }
            else if (allowedOrigin == null)
            {
                return Refuse(401, "no_token", "Missing 'Authorization: Bearer <token>'. The token is in GameData/SkizzyProtocol/PluginData/auth.token.");
            }

            return new HandshakeDecision { Status = 101, Reason = "ok", SubProtocol = sub, TokenVerified = tokenOk, Origin = allowedOrigin };
        }

        public static bool HostAllowed(string host, int port)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string suffix = ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!host.EndsWith(suffix, StringComparison.Ordinal)) return false;
            string name = host.Substring(0, host.Length - suffix.Length);
            return name == "127.0.0.1" || string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase) || name == "[::1]";
        }

        private static bool KeyValid(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length != 24) return false;
            try { return Convert.FromBase64String(key).Length == 16; }
            catch (FormatException) { return false; }
        }

        private static bool TokenListContains(string headerValue, string token)
        {
            if (headerValue == null) return false;
            foreach (var part in headerValue.Split(','))
                if (string.Equals(part.Trim(), token, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Constant-time comparison (time depends only on the presented token's length).</summary>
        public static bool TokenEquals(string presented, byte[] expected)
        {
            if (presented == null || expected == null || expected.Length == 0) return false;
            int diff = presented.Length ^ expected.Length;
            for (int i = 0; i < presented.Length; i++)
            {
                int e = i < expected.Length ? expected[i] : 0;
                diff |= presented[i] ^ e;
            }
            return diff == 0;
        }

        public static string AcceptKey(string key)
        {
            using (var sha = SHA1.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + Guid)));
        }

        public static byte[] BuildAccept(HandshakeRequest r, HandshakeDecision d)
        {
            var sb = new StringBuilder(256);
            sb.Append("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ");
            sb.Append(AcceptKey(r.Header("Sec-WebSocket-Key"))).Append("\r\n");
            if (d.SubProtocol != null) sb.Append("Sec-WebSocket-Protocol: ").Append(d.SubProtocol).Append("\r\n");
            sb.Append("Server: skizzy-host/").Append(Skizzy.Protocol.ProtocolInfo.HostVersion).Append("\r\n\r\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        public static byte[] BuildRefusal(HandshakeDecision d)
        {
            string body = d.Message + "\n";
            var sb = new StringBuilder(256);
            sb.Append("HTTP/1.1 ").Append(d.Status).Append(' ').Append(StatusText(d.Status)).Append("\r\n");
            sb.Append("Content-Type: text/plain; charset=utf-8\r\nConnection: close\r\nX-Skizzy-Reason: ").Append(d.Reason).Append("\r\n");
            if (d.Status == 426) sb.Append("Sec-WebSocket-Version: 13\r\n");
            sb.Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(body)).Append("\r\n\r\n").Append(body);
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        private static HandshakeDecision Refuse(int status, string reason, string message)
        {
            return new HandshakeDecision { Status = status, Reason = reason, Message = message };
        }

        private static string StatusText(int s)
        {
            switch (s)
            {
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 426: return "Upgrade Required";
                case 503: return "Service Unavailable";
                default: return "Error";
            }
        }
    }
}
