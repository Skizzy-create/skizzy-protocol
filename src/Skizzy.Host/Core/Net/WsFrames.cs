using System;
using System.IO;
using Skizzy.Protocol.Json;

namespace Skizzy.Host.Core.Net
{
    public static class WsOpcode
    {
        public const byte Continuation = 0x0;
        public const byte Text = 0x1;
        public const byte Binary = 0x2;
        public const byte Close = 0x8;
        public const byte Ping = 0x9;
        public const byte Pong = 0xA;
    }

    /// <summary>WebSocket close codes used by the host (RFC 6455 §7.4 plus private 4000-4999).</summary>
    public static class WsClose
    {
        public const int Normal = 1000;
        public const int GoingAway = 1001;
        public const int ProtocolError = 1002;
        public const int UnsupportedData = 1003;
        public const int InvalidPayload = 1007;
        public const int PolicyViolation = 1008;
        public const int TooBig = 1009;
        public const int InternalError = 1011;
        /// <summary>Private range: 4000 + (-code - 32000) for an error kind, e.g. 4001 UNAUTHENTICATED, 4024 SLOW_CONSUMER.</summary>
        public static int ForErrorCode(int rpcCode) { return 4000 + (-rpcCode - 32000); }
    }

    /// <summary>Raised by the frame reader for protocol violations; carries the close code to send.</summary>
    public sealed class WsProtocolException : Exception
    {
        public readonly int CloseCode;
        public WsProtocolException(int closeCode, string message) : base(message) { CloseCode = closeCode; }
    }

    /// <summary>
    /// Reads client frames (RFC 6455 §5) and assembles data messages into a caller-supplied buffer.
    /// Enforces: client masking, no RSV bits (no extensions negotiated), control frames ≤125 bytes and unfragmented,
    /// message size cap, valid UTF-8 for text. Allocation-free per frame after construction.
    /// </summary>
    public sealed class WsFrameReader
    {
        private readonly Stream _s;
        private readonly int _maxMessage;
        private readonly byte[] _hdr = new byte[14];
        private readonly byte[] _ctrl = new byte[125];

        public byte LastControlOpcode;
        public int LastControlLength;
        public byte[] ControlPayload => _ctrl;
        public long BytesRead;

        public WsFrameReader(Stream s, int maxMessage)
        {
            _s = s;
            _maxMessage = maxMessage;
        }

        /// <summary>
        /// Reads until a complete data message or a control frame arrives. Returns the opcode: Text/Binary (payload
        /// in <paramref name="message"/>), or Ping/Pong/Close (payload in <see cref="ControlPayload"/>, length
        /// <see cref="LastControlLength"/>). Throws <see cref="EndOfStreamException"/> on EOF.
        /// </summary>
        // skizzy:hot WsFrameReader.Read
        public byte Read(ByteBuffer message, ref byte pendingOpcode)
        {
            while (true)
            {
                ReadExactly(_hdr, 0, 2);
                byte b0 = _hdr[0], b1 = _hdr[1];
                bool fin = (b0 & 0x80) != 0;
                if ((b0 & 0x70) != 0) throw new WsProtocolException(WsClose.ProtocolError, "RSV bits set without a negotiated extension");
                byte op = (byte)(b0 & 0x0F);
                bool masked = (b1 & 0x80) != 0;
                if (!masked) throw new WsProtocolException(WsClose.ProtocolError, "client frames must be masked");
                long len = b1 & 0x7F;
                if (len == 126)
                {
                    ReadExactly(_hdr, 2, 2);
                    len = (_hdr[2] << 8) | _hdr[3];
                }
                else if (len == 127)
                {
                    ReadExactly(_hdr, 2, 8);
                    if ((_hdr[2] & 0x80) != 0) throw new WsProtocolException(WsClose.ProtocolError, "invalid payload length");
                    len = 0;
                    for (int i = 2; i < 10; i++) len = (len << 8) | _hdr[i];
                }
                ReadExactly(_hdr, 10, 4);
                byte m0 = _hdr[10], m1 = _hdr[11], m2 = _hdr[12], m3 = _hdr[13];

                if (op >= 0x8)
                {
                    if (!fin || len > 125) throw new WsProtocolException(WsClose.ProtocolError, "invalid control frame");
                    if (op != WsOpcode.Close && op != WsOpcode.Ping && op != WsOpcode.Pong)
                        throw new WsProtocolException(WsClose.ProtocolError, "unknown control opcode");
                    int n = (int)len;
                    ReadExactly(_ctrl, 0, n);
                    Unmask(_ctrl, 0, n, m0, m1, m2, m3, 0);
                    LastControlOpcode = op;
                    LastControlLength = n;
                    return op;
                }

                if (op == WsOpcode.Continuation)
                {
                    if (pendingOpcode == 0) throw new WsProtocolException(WsClose.ProtocolError, "continuation without a message");
                }
                else if (op == WsOpcode.Text || op == WsOpcode.Binary)
                {
                    if (pendingOpcode != 0) throw new WsProtocolException(WsClose.ProtocolError, "new message before the previous one finished");
                    pendingOpcode = op;
                    message.Clear();
                }
                else throw new WsProtocolException(WsClose.ProtocolError, "unknown data opcode");

                if (message.Length + len > _maxMessage)
                    throw new WsProtocolException(WsClose.TooBig, "message exceeds " + _maxMessage + " bytes");
                int count = (int)len;
                int start = message.Length;
                message.Ensure(count);
                ReadExactly(message.Data, start, count);
                Unmask(message.Data, start, count, m0, m1, m2, m3, 0);
                message.Length = start + count;

                if (fin)
                {
                    byte done = pendingOpcode;
                    pendingOpcode = 0;
                    if (done == WsOpcode.Text && !JsonParser.IsValidUtf8(message.Data, 0, message.Length))
                        throw new WsProtocolException(WsClose.InvalidPayload, "text frame is not valid UTF-8");
                    return done;
                }
            }
        }

        private static void Unmask(byte[] d, int off, int n, byte m0, byte m1, byte m2, byte m3, int phase)
        {
            for (int i = 0; i < n; i++)
            {
                switch ((i + phase) & 3)
                {
                    case 0: d[off + i] ^= m0; break;
                    case 1: d[off + i] ^= m1; break;
                    case 2: d[off + i] ^= m2; break;
                    default: d[off + i] ^= m3; break;
                }
            }
        }

        private void ReadExactly(byte[] buf, int off, int count)
        {
            while (count > 0)
            {
                int r = _s.Read(buf, off, count);
                if (r <= 0) throw new EndOfStreamException();
                off += r;
                count -= r;
                BytesRead += r;
            }
        }
    }

    /// <summary>Writes unmasked server frames. One instance per writer thread; allocation-free.</summary>
    public sealed class WsFrameWriter
    {
        private readonly byte[] _hdr = new byte[10];

        // skizzy:hot WsFrameWriter.Write
        public int Write(Stream s, byte opcode, byte[] data, int off, int len)
        {
            int h = 2;
            _hdr[0] = (byte)(0x80 | opcode);
            if (len < 126) _hdr[1] = (byte)len;
            else if (len <= 0xFFFF)
            {
                _hdr[1] = 126;
                _hdr[2] = (byte)(len >> 8);
                _hdr[3] = (byte)len;
                h = 4;
            }
            else
            {
                _hdr[1] = 127;
                long l = len;
                for (int i = 9; i >= 2; i--) { _hdr[i] = (byte)l; l >>= 8; }
                h = 10;
            }
            s.Write(_hdr, 0, h);
            if (len > 0) s.Write(data, off, len);
            return h + len;
        }

        /// <summary>Builds a close payload: 2-byte code plus a UTF-8 reason truncated to fit 125 bytes.</summary>
        public static int BuildClosePayload(byte[] into, int code, string reason)
        {
            into[0] = (byte)(code >> 8);
            into[1] = (byte)code;
            int n = 2;
            if (reason != null)
            {
                for (int i = 0; i < reason.Length && n < 123; i++)
                {
                    char c = reason[i];
                    into[n++] = c < 0x80 ? (byte)c : (byte)'?';
                }
            }
            return n;
        }
    }
}
