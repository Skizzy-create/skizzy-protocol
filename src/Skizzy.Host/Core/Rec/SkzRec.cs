using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;

namespace Skizzy.Host.Core.Rec
{
    /// <summary>
    /// Table-driven CRC-32 (IEEE 802.3: reflected polynomial 0xEDB88320, init and xorout 0xFFFFFFFF).
    /// <c>Compute</c> returns the standard final value ("123456789" gives 0xCBF43926). <c>Update</c> works on the
    /// running register (pre-inverted form): start from <c>Initial</c>, chain <c>Update</c> calls, then apply
    /// <c>Finish</c> (xor 0xFFFFFFFF) to get the final value.
    /// </summary>
    public static class Crc32
    {
        public const uint Initial = 0xFFFFFFFFu;

        private static readonly uint[] Table = BuildTable();

        public static uint Compute(byte[] data, int offset, int count)
        {
            return Finish(Update(Initial, data, offset, count));
        }

        public static uint Update(uint crc, byte[] data, int offset, int count)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }
            if (offset < 0 || count < 0 || offset > data.Length - count)
            {
                throw new ArgumentOutOfRangeException("offset");
            }
            uint[] t = Table;
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                crc = t[(crc ^ data[i]) & 0xff] ^ (crc >> 8);
            }
            return crc;
        }

        public static uint Finish(uint crc)
        {
            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                t[i] = c;
            }
            return t;
        }
    }

    /// <summary>One recorded channel: an id, a name and the field names that follow ut in each record.</summary>
    public sealed class SkzRecChannel
    {
        public int Id;
        public string Name;
        public string[] Fields;
    }

    /// <summary>One record read back from a data chunk; <c>Fields</c> excludes ut.</summary>
    public sealed class SkzRecRecord
    {
        public double Ut;
        public object[] Fields;
    }

    /// <summary>Index entry for one data chunk; <c>Offset</c> is the file offset of the chunk's type byte.</summary>
    public sealed class SkzRecIndexEntry
    {
        public int ChunkSeq;
        public int ChannelId;
        public double UtFrom;
        public double UtTo;
        public int Count;
        public long Offset;
    }

    /// <summary>A labelled point in time, written in the footer.</summary>
    public sealed class SkzRecMark
    {
        public double Ut;
        public string Label;
        public string Note;
    }

    /// <summary>Constants and encoders shared by the writer, reader and repair.</summary>
    internal static class SkzRecFormat
    {
        public const byte ChunkHeader = 1;
        public const byte ChunkData = 2;
        public const byte ChunkIndex = 3;
        public const byte ChunkFooter = 4;
        public const int ChunkOverhead = 9; // type + length + crc
        public const int TrailerLength = 16;
        public const int IndexEvery = 64;
        public const string Codec = "raw";

        public static readonly byte[] Magic = Encoding.ASCII.GetBytes("SKZREC1\n");
        public static readonly byte[] EndMagic = Encoding.ASCII.GetBytes("SKZEND1\n");

        /// <summary>Writes type, LE length, body (a then b), LE CRC. Throws on IO errors.</summary>
        public static void WriteChunk(Stream s, byte type, byte[] a, int aLen, byte[] b, int bLen)
        {
            long len = (long)aLen + bLen;
            if (len > uint.MaxValue)
            {
                throw new ArgumentException("chunk too large");
            }
            var head = new byte[5];
            head[0] = type;
            PutU32(head, 1, (uint)len);
            uint crc = Crc32.Update(Crc32.Initial, head, 0, 5);
            crc = Crc32.Update(crc, a, 0, aLen);
            if (bLen > 0)
            {
                crc = Crc32.Update(crc, b, 0, bLen);
            }
            var tail = new byte[4];
            PutU32(tail, 0, Crc32.Finish(crc));
            s.Write(head, 0, 5);
            s.Write(a, 0, aLen);
            if (bLen > 0)
            {
                s.Write(b, 0, bLen);
            }
            s.Write(tail, 0, 4);
        }

        public static byte[] Trailer(long footerOffset)
        {
            var t = new byte[TrailerLength];
            PutU32(t, 0, (uint)footerOffset);
            PutU32(t, 4, (uint)((ulong)footerOffset >> 32));
            System.Buffer.BlockCopy(EndMagic, 0, t, 8, 8);
            return t;
        }

        public static void WriteHeaderBody(MsgPackWriter w, string created, string profile, string title, IList<SkzRecChannel> channels, IDictionary<string, object> meta)
        {
            w.WriteMapHeader(7);
            w.WriteString("format");
            w.WriteString("skzrec");
            w.WriteString("version");
            w.WriteInt(1);
            w.WriteString("created");
            w.WriteString(created);
            w.WriteString("profile");
            w.WriteString(profile);
            w.WriteString("title");
            w.WriteString(title);
            w.WriteString("channels");
            w.WriteArrayHeader(channels.Count);
            for (int i = 0; i < channels.Count; i++)
            {
                SkzRecChannel c = channels[i];
                string[] fields = c.Fields ?? new string[0];
                w.WriteMapHeader(3);
                w.WriteString("id");
                w.WriteInt(c.Id);
                w.WriteString("name");
                w.WriteString(c.Name);
                w.WriteString("fields");
                w.WriteArrayHeader(fields.Length);
                for (int k = 0; k < fields.Length; k++)
                {
                    w.WriteString(fields[k]);
                }
            }
            w.WriteString("meta");
            if (meta == null)
            {
                w.WriteMapHeader(0);
            }
            else
            {
                w.WriteValue(meta);
            }
        }

        public static void WriteIndexBody(MsgPackWriter w, IList<SkzRecIndexEntry> entries)
        {
            w.WriteArrayHeader(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                SkzRecIndexEntry e = entries[i];
                w.WriteArrayHeader(6);
                w.WriteInt(e.ChunkSeq);
                w.WriteInt(e.ChannelId);
                w.WriteDouble(e.UtFrom);
                w.WriteDouble(e.UtTo);
                w.WriteInt(e.Count);
                w.WriteInt(e.Offset);
            }
        }

        /// <summary>Footer map; <paramref name="summary"/> is an already-encoded msgpack map.</summary>
        public static void WriteFooterBody(MsgPackWriter w, IList<long> indexOffsets, long chunks, long records, IList<SkzRecMark> marks, MsgPackWriter summary, string closed)
        {
            w.WriteMapHeader(6);
            w.WriteString("indexOffsets");
            w.WriteArrayHeader(indexOffsets.Count);
            for (int i = 0; i < indexOffsets.Count; i++)
            {
                w.WriteInt(indexOffsets[i]);
            }
            w.WriteString("chunks");
            w.WriteInt(chunks);
            w.WriteString("records");
            w.WriteInt(records);
            w.WriteString("marks");
            w.WriteArrayHeader(marks.Count);
            for (int i = 0; i < marks.Count; i++)
            {
                w.WriteArrayHeader(3);
                w.WriteDouble(marks[i].Ut);
                w.WriteString(marks[i].Label);
                w.WriteString(marks[i].Note);
            }
            w.WriteString("summary");
            w.WriteRaw(summary.Buffer, 0, summary.Length);
            w.WriteString("closed");
            w.WriteString(closed);
        }

        public static void PutU32(byte[] b, int i, uint v)
        {
            b[i] = (byte)v;
            b[i + 1] = (byte)(v >> 8);
            b[i + 2] = (byte)(v >> 16);
            b[i + 3] = (byte)(v >> 24);
        }

        public static uint GetU32(byte[] b, int i)
        {
            return b[i] | ((uint)b[i + 1] << 8) | ((uint)b[i + 2] << 16) | ((uint)b[i + 3] << 24);
        }

        public static bool SameBytes(byte[] a, int aOffset, byte[] b)
        {
            for (int i = 0; i < b.Length; i++)
            {
                if (a[aOffset + i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }

        public static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        public static long AsLong(object v, string what)
        {
            if (v is long l)
            {
                return l;
            }
            if (v is double d && !double.IsNaN(d) && !double.IsInfinity(d))
            {
                return (long)d;
            }
            throw Bad("bad " + what);
        }

        public static int AsInt(object v, string what)
        {
            long l = AsLong(v, what);
            if (l < int.MinValue || l > int.MaxValue)
            {
                throw Bad(what + " out of range");
            }
            return (int)l;
        }

        public static double AsDouble(object v, string what)
        {
            if (v is double d)
            {
                return d;
            }
            if (v is long l)
            {
                return l;
            }
            if (v is ulong u)
            {
                return u;
            }
            throw Bad("bad " + what);
        }

        public static object[] AsArray(object v, string what)
        {
            if (v is object[] a)
            {
                return a;
            }
            throw Bad("bad " + what);
        }

        public static Dictionary<string, object> AsMap(object v, string what)
        {
            if (v is Dictionary<string, object> m)
            {
                return m;
            }
            throw Bad("bad " + what);
        }

        public static FormatException Bad(string message)
        {
            return new FormatException(".skzrec: " + message);
        }
    }

    /// <summary>
    /// Writes a <c>.skzrec</c> recording: magic, header chunk, data chunks (one msgpack payload of records per
    /// channel), an index chunk every 64 data chunks, then footer and trailer on <c>Close</c>. Records are buffered
    /// per channel and closed into a data chunk by size (<c>MaxChunkBytes</c>) or time span (<c>MaxChunkSpanS</c>),
    /// or by <c>Flush</c>. Any IO failure sets <c>Failed</c>/<c>Error</c> and turns every later call into a no-op;
    /// the writer never throws for IO. After <c>Close</c> calls are no-ops too. Not thread-safe.
    /// </summary>
    public sealed class SkzRecWriter : IDisposable
    {
        private sealed class PendingChannel
        {
            public SkzRecChannel Channel;
            public MsgPackWriter Buffer;
            public int Count;
            public double UtFrom;
            public double UtTo;
        }

        private readonly Dictionary<int, PendingChannel> _channels = new Dictionary<int, PendingChannel>();
        private readonly List<PendingChannel> _order = new List<PendingChannel>();
        private readonly List<SkzRecIndexEntry> _unindexed = new List<SkzRecIndexEntry>();
        private readonly List<long> _indexOffsets = new List<long>();
        private readonly List<SkzRecMark> _marks = new List<SkzRecMark>();
        private readonly MsgPackWriter _scratch = new MsgPackWriter();
        private readonly MsgPackWriter _sink = new MsgPackWriter();
        private Stream _stream;
        private PendingChannel _open;
        private int _openStart;
        private double _openUt;
        private int _seq;
        private long _chunkRecords;
        private bool _closed;

        /// <summary>A channel buffer at or above this size is closed into a data chunk (default 1 MiB).</summary>
        public int MaxChunkBytes = 1024 * 1024;

        /// <summary>A channel buffer spanning at least this many seconds of ut is closed (default 5 s).</summary>
        public double MaxChunkSpanS = 5.0;

        /// <summary>
        /// Writes magic and header to <paramref name="stream"/> right away; the writer owns the stream. Offsets are
        /// counted from the magic, so the stream should be at position 0.
        /// </summary>
        public SkzRecWriter(Stream stream, string created, string profile, string title, IList<SkzRecChannel> channels, IDictionary<string, object> meta)
            : this(CheckStream(stream), BuildHeader(created, profile, title, channels, meta), channels)
        {
        }

        private SkzRecWriter(Stream stream, MsgPackWriter header, IList<SkzRecChannel> channels)
        {
            _stream = stream;
            for (int i = 0; i < channels.Count; i++)
            {
                var p = new PendingChannel { Channel = channels[i], Buffer = new MsgPackWriter() };
                _channels.Add(channels[i].Id, p);
                _order.Add(p);
            }
            if (TryWrite(SkzRecFormat.Magic, 0, SkzRecFormat.Magic.Length))
            {
                TryWriteChunk(SkzRecFormat.ChunkHeader, header.Buffer, header.Length, null, 0);
            }
        }

        /// <summary>Creates a new file (FileMode.CreateNew, FileShare.Read). Failing to open the file throws.</summary>
        public static SkzRecWriter Create(string path, string created, string profile, string title, IList<SkzRecChannel> channels, IDictionary<string, object> meta)
        {
            if (path == null)
            {
                throw new ArgumentNullException("path");
            }
            MsgPackWriter header = BuildHeader(created, profile, title, channels, meta);
            var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
            return new SkzRecWriter(fs, header, channels);
        }

        public bool Failed { get; private set; }

        public string Error { get; private set; }

        /// <summary>Bytes successfully written, including the magic.</summary>
        public long BytesWritten { get; private set; }

        /// <summary>Data chunks written.</summary>
        public int Chunks { get; private set; }

        /// <summary>Records accepted by <c>EndRecord</c>.</summary>
        public long Records { get; private set; }

        public bool IsClosed
        {
            get { return _closed; }
        }

        /// <summary>
        /// Starts a record: writes the array header (fieldCount + 1) and ut into the channel's buffer and returns that
        /// buffer. The caller writes exactly <paramref name="fieldCount"/> values, then calls <c>EndRecord</c>
        /// (or <c>CancelRecord</c>). After a failure or close a throwaway writer is returned.
        /// </summary>
        public MsgPackWriter BeginRecord(int channelId, double ut, int fieldCount)
        {
            if (Failed || _closed)
            {
                _sink.Clear();
                return _sink;
            }
            if (_open != null)
            {
                throw new InvalidOperationException("BeginRecord called twice without EndRecord");
            }
            if (fieldCount < 0)
            {
                throw new ArgumentOutOfRangeException("fieldCount");
            }
            if (double.IsNaN(ut) || double.IsInfinity(ut))
            {
                throw new ArgumentOutOfRangeException("ut");
            }
            PendingChannel p;
            if (!_channels.TryGetValue(channelId, out p))
            {
                throw new ArgumentException("unknown channel id " + channelId, "channelId");
            }
            _open = p;
            _openStart = p.Buffer.Length;
            _openUt = ut;
            p.Buffer.WriteArrayHeader(fieldCount + 1);
            p.Buffer.WriteDouble(ut);
            return p.Buffer;
        }

        public void EndRecord()
        {
            if (Failed || _closed)
            {
                _open = null;
                return;
            }
            PendingChannel p = _open;
            if (p == null)
            {
                throw new InvalidOperationException("EndRecord without BeginRecord");
            }
            _open = null;
            double ut = _openUt;
            if (p.Count == 0)
            {
                p.UtFrom = ut;
                p.UtTo = ut;
            }
            else
            {
                if (ut < p.UtFrom)
                {
                    p.UtFrom = ut;
                }
                if (ut > p.UtTo)
                {
                    p.UtTo = ut;
                }
            }
            p.Count++;
            Records++;
            if (p.Buffer.Length >= MaxChunkBytes || ut - p.UtFrom >= MaxChunkSpanS)
            {
                CloseChunk(p);
            }
        }

        /// <summary>Discards the record started by the last <c>BeginRecord</c> (e.g. after the caller threw).</summary>
        public void CancelRecord()
        {
            if (_open == null)
            {
                return;
            }
            _open.Buffer.Truncate(_openStart);
            _open = null;
        }

        /// <summary>Kept in memory and written in the footer.</summary>
        public void Mark(double ut, string label, string note)
        {
            if (Failed || _closed)
            {
                return;
            }
            _marks.Add(new SkzRecMark { Ut = ut, Label = label ?? string.Empty, Note = note });
        }

        /// <summary>Closes every non-empty channel buffer into data chunks and flushes the stream.</summary>
        public void Flush()
        {
            if (Failed || _closed)
            {
                return;
            }
            if (_open != null)
            {
                throw new InvalidOperationException("Flush called while a record is open");
            }
            FlushChannels();
            TryFlushStream();
        }

        /// <summary>
        /// Flushes, writes the final index chunk, footer and trailer, and disposes the stream. Idempotent. An open
        /// record is discarded. Only an unsupported value in <paramref name="summary"/> throws (before anything is
        /// written, leaving the writer open).
        /// </summary>
        public void Close(IDictionary<string, object> summary, string closed)
        {
            if (_closed)
            {
                return;
            }
            MsgPackWriter sum = null;
            if (!Failed)
            {
                sum = new MsgPackWriter();
                if (summary == null)
                {
                    sum.WriteMapHeader(0);
                }
                else
                {
                    sum.WriteValue(summary);
                }
            }
            _closed = true;
            CancelRecord();
            if (!Failed)
            {
                FlushChannels();
                WriteIndexChunk();
                long footerOffset = BytesWritten;
                _scratch.Clear();
                SkzRecFormat.WriteFooterBody(_scratch, _indexOffsets, Chunks, _chunkRecords, _marks, sum, closed);
                if (TryWriteChunk(SkzRecFormat.ChunkFooter, _scratch.Buffer, _scratch.Length, null, 0))
                {
                    byte[] trailer = SkzRecFormat.Trailer(footerOffset);
                    if (TryWrite(trailer, 0, trailer.Length))
                    {
                        TryFlushStream();
                    }
                }
            }
            Stream s = _stream;
            _stream = null;
            if (s != null)
            {
                try
                {
                    s.Dispose();
                }
                catch (Exception e) when (IsIoError(e))
                {
                    if (!Failed)
                    {
                        Failed = true;
                        Error = e.Message;
                    }
                }
            }
        }

        public void Dispose()
        {
            if (!_closed)
            {
                Close(null, null);
            }
        }

        private void FlushChannels()
        {
            for (int i = 0; i < _order.Count && !Failed; i++)
            {
                if (_order[i].Count > 0)
                {
                    CloseChunk(_order[i]);
                }
            }
        }

        private void CloseChunk(PendingChannel p)
        {
            if (p.Count == 0 || Failed)
            {
                return;
            }
            long offset = BytesWritten;
            MsgPackWriter s = _scratch;
            s.Clear();
            s.WriteArrayHeader(7);
            s.WriteInt(_seq);
            s.WriteInt(p.Channel.Id);
            s.WriteDouble(p.UtFrom);
            s.WriteDouble(p.UtTo);
            s.WriteInt(p.Count);
            s.WriteString(SkzRecFormat.Codec);
            s.WriteBinHeader(p.Buffer.Length);
            if (!TryWriteChunk(SkzRecFormat.ChunkData, s.Buffer, s.Length, p.Buffer.Buffer, p.Buffer.Length))
            {
                return;
            }
            _unindexed.Add(new SkzRecIndexEntry
            {
                ChunkSeq = _seq,
                ChannelId = p.Channel.Id,
                UtFrom = p.UtFrom,
                UtTo = p.UtTo,
                Count = p.Count,
                Offset = offset,
            });
            _seq++;
            Chunks++;
            _chunkRecords += p.Count;
            p.Buffer.Clear();
            p.Count = 0;
            if (_unindexed.Count >= SkzRecFormat.IndexEvery)
            {
                WriteIndexChunk();
            }
        }

        private void WriteIndexChunk()
        {
            if (_unindexed.Count == 0 || Failed)
            {
                return;
            }
            long offset = BytesWritten;
            _scratch.Clear();
            SkzRecFormat.WriteIndexBody(_scratch, _unindexed);
            if (TryWriteChunk(SkzRecFormat.ChunkIndex, _scratch.Buffer, _scratch.Length, null, 0))
            {
                _indexOffsets.Add(offset);
                _unindexed.Clear();
            }
        }

        private bool TryWrite(byte[] data, int offset, int count)
        {
            if (Failed)
            {
                return false;
            }
            try
            {
                _stream.Write(data, offset, count);
                BytesWritten += count;
                return true;
            }
            catch (Exception e) when (IsIoError(e))
            {
                Fail(e);
                return false;
            }
        }

        private bool TryWriteChunk(byte type, byte[] a, int aLen, byte[] b, int bLen)
        {
            if (Failed)
            {
                return false;
            }
            try
            {
                SkzRecFormat.WriteChunk(_stream, type, a, aLen, b, bLen);
                BytesWritten += SkzRecFormat.ChunkOverhead + aLen + bLen;
                return true;
            }
            catch (Exception e) when (IsIoError(e))
            {
                Fail(e);
                return false;
            }
        }

        private void TryFlushStream()
        {
            if (Failed)
            {
                return;
            }
            try
            {
                _stream.Flush();
            }
            catch (Exception e) when (IsIoError(e))
            {
                Fail(e);
            }
        }

        private void Fail(Exception e)
        {
            if (Failed)
            {
                return;
            }
            Failed = true;
            Error = e.Message;
            _open = null;
            for (int i = 0; i < _order.Count; i++)
            {
                _order[i].Buffer.Clear();
                _order[i].Count = 0;
            }
            Stream s = _stream;
            _stream = null;
            if (s != null)
            {
                try
                {
                    s.Dispose();
                }
                catch (Exception)
                {
                    // Already failed; disposing quietly is all that is left to do.
                }
            }
        }

        private static bool IsIoError(Exception e)
        {
            return e is IOException || e is UnauthorizedAccessException || e is ObjectDisposedException || e is NotSupportedException;
        }

        private static Stream CheckStream(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }
            return stream;
        }

        private static MsgPackWriter BuildHeader(string created, string profile, string title, IList<SkzRecChannel> channels, IDictionary<string, object> meta)
        {
            if (channels == null)
            {
                throw new ArgumentNullException("channels");
            }
            var ids = new HashSet<int>();
            for (int i = 0; i < channels.Count; i++)
            {
                if (channels[i] == null)
                {
                    throw new ArgumentException("channel " + i + " is null", "channels");
                }
                if (!ids.Add(channels[i].Id))
                {
                    throw new ArgumentException("duplicate channel id " + channels[i].Id, "channels");
                }
            }
            var w = new MsgPackWriter();
            SkzRecFormat.WriteHeaderBody(w, created, profile, title, channels, meta);
            return w;
        }
    }

    /// <summary>
    /// Reads a <c>.skzrec</c> recording. A clean file is opened via trailer, footer and index chunks; anything else
    /// (unclosed, truncated, CRC-bad) falls back to a sequential scan up to the first bad chunk and sets
    /// <c>Repaired</c>. Records are read lazily, only from the chunks the index says overlap the request.
    /// </summary>
    public sealed class SkzRecReader : IDisposable
    {
        private struct RawChunk
        {
            public byte Type;
            public byte[] Body;
            public long Offset;
            public long End;
        }

        private sealed class FooterInfo
        {
            public List<long> IndexOffsets = new List<long>();
            public long Chunks;
            public List<SkzRecMark> Marks = new List<SkzRecMark>();
            public Dictionary<string, object> Summary;
            public string Closed;
        }

        private readonly bool _leaveOpen;
        private readonly long _length;
        private readonly byte[] _small = new byte[16];
        private Stream _stream;
        private long _headerEnd;
        private List<SkzRecIndexEntry> _index = new List<SkzRecIndexEntry>();
        private List<long> _indexOffsets = new List<long>();
        private List<SkzRecMark> _marks = new List<SkzRecMark>();
        private List<SkzRecChannel> _channels = new List<SkzRecChannel>();

        private SkzRecReader(Stream stream, bool leaveOpen)
        {
            _stream = stream;
            _leaveOpen = leaveOpen;
            _length = stream.Length;
            Summary = new Dictionary<string, object>(StringComparer.Ordinal);
            if (_length < SkzRecFormat.Magic.Length || !ReadAt(0, _small, 0, 8) || !SkzRecFormat.SameBytes(_small, 0, SkzRecFormat.Magic))
            {
                throw new FormatException("not a .skzrec file");
            }
            RawChunk header;
            if (!TryReadChunk(SkzRecFormat.Magic.Length, out header) || header.Type != SkzRecFormat.ChunkHeader)
            {
                throw SkzRecFormat.Bad("header chunk is missing or corrupt");
            }
            ParseHeader(header.Body);
            _headerEnd = header.End;
            if (!TryLoadIndexed())
            {
                Scan();
                Repaired = true;
            }
            _index.Sort((x, y) => x.Offset.CompareTo(y.Offset));
            UtFrom = double.NaN;
            UtTo = double.NaN;
            for (int i = 0; i < _index.Count; i++)
            {
                SkzRecIndexEntry e = _index[i];
                RecordCount += e.Count;
                if (i == 0 || e.UtFrom < UtFrom)
                {
                    UtFrom = e.UtFrom;
                }
                if (i == 0 || e.UtTo > UtTo)
                {
                    UtTo = e.UtTo;
                }
            }
        }

        /// <summary>Opens a file for reading (FileShare.ReadWrite, so a file still being written can be read).</summary>
        public static SkzRecReader Open(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException("path");
            }
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
            try
            {
                return new SkzRecReader(fs, false);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        /// <summary>Reads from a seekable stream, which the reader disposes in <c>Dispose</c>.</summary>
        public static SkzRecReader Open(Stream stream)
        {
            return Open(stream, false);
        }

        internal static SkzRecReader Open(Stream stream, bool leaveOpen)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }
            if (!stream.CanRead || !stream.CanSeek)
            {
                throw new ArgumentException("stream must be readable and seekable", "stream");
            }
            return new SkzRecReader(stream, leaveOpen);
        }

        /// <summary>True when the file was not clean and was read by sequential scan.</summary>
        public bool Repaired { get; private set; }

        public Dictionary<string, object> Header { get; private set; }

        public Dictionary<string, object> Meta { get; private set; }

        public string Created { get; private set; }

        public string Profile { get; private set; }

        public string Title { get; private set; }

        public IList<SkzRecChannel> Channels
        {
            get { return _channels.AsReadOnly(); }
        }

        /// <summary>Data chunks in file order.</summary>
        public IList<SkzRecIndexEntry> Index
        {
            get { return _index.AsReadOnly(); }
        }

        /// <summary>Offsets of the index chunks (from the footer; for a scanned file, those found by the scan).</summary>
        public IList<long> IndexOffsets
        {
            get { return _indexOffsets.AsReadOnly(); }
        }

        public IList<SkzRecMark> Marks
        {
            get { return _marks.AsReadOnly(); }
        }

        /// <summary>The footer summary; empty if there is none.</summary>
        public Dictionary<string, object> Summary { get; private set; }

        /// <summary>The footer "closed" string; null if there is none.</summary>
        public string Closed { get; private set; }

        public long RecordCount { get; private set; }

        public double UtFrom { get; private set; }

        public double UtTo { get; private set; }

        /// <summary>
        /// Length of the valid prefix: the whole file when clean; otherwise the end of the last good
        /// header/data/index chunk (a footer found by the scan is excluded, since Repair rewrites it).
        /// </summary>
        public long ValidLength { get; private set; }

        public SkzRecChannel Channel(string name)
        {
            for (int i = 0; i < _channels.Count; i++)
            {
                if (string.Equals(_channels[i].Name, name, StringComparison.Ordinal))
                {
                    return _channels[i];
                }
            }
            return null;
        }

        public IEnumerable<SkzRecRecord> Records(int channelId)
        {
            return Records(channelId, double.NegativeInfinity, double.PositiveInfinity);
        }

        /// <summary>
        /// Records of one channel with fromUt &lt;= ut &lt;= toUt, in file order. Only chunks whose index range
        /// overlaps the request are read. A chunk that fails its CRC throws FormatException.
        /// </summary>
        public IEnumerable<SkzRecRecord> Records(int channelId, double fromUt, double toUt)
        {
            for (int i = 0; i < _index.Count; i++)
            {
                SkzRecIndexEntry e = _index[i];
                if (e.ChannelId != channelId || e.UtTo < fromUt || e.UtFrom > toUt)
                {
                    continue;
                }
                byte[] payload = ReadPayload(e);
                var r = new MsgPackReader(payload);
                while (!r.End)
                {
                    int n = r.ReadArrayHeader();
                    if (n < 1)
                    {
                        throw SkzRecFormat.Bad("empty record");
                    }
                    double ut = r.ReadDouble();
                    if (ut < fromUt || ut > toUt)
                    {
                        for (int k = 1; k < n; k++)
                        {
                            r.Skip();
                        }
                        continue;
                    }
                    var fields = new object[n - 1];
                    for (int k = 0; k < fields.Length; k++)
                    {
                        fields[k] = r.ReadObject();
                    }
                    yield return new SkzRecRecord { Ut = ut, Fields = fields };
                }
            }
        }

        public void Dispose()
        {
            Stream s = _stream;
            _stream = null;
            if (s != null && !_leaveOpen)
            {
                s.Dispose();
            }
        }

        private byte[] ReadPayload(SkzRecIndexEntry e)
        {
            if (_stream == null)
            {
                throw new ObjectDisposedException("SkzRecReader");
            }
            RawChunk c;
            if (!TryReadChunk(e.Offset, out c) || c.Type != SkzRecFormat.ChunkData)
            {
                throw SkzRecFormat.Bad("data chunk at offset " + e.Offset + " is corrupt");
            }
            var r = new MsgPackReader(c.Body);
            if (r.ReadArrayHeader() < 7)
            {
                throw SkzRecFormat.Bad("bad data chunk");
            }
            for (int k = 0; k < 5; k++)
            {
                r.Skip();
            }
            string codec = r.ReadString();
            if (codec != SkzRecFormat.Codec)
            {
                throw SkzRecFormat.Bad("unsupported codec " + (codec ?? "nil"));
            }
            return r.ReadBin() ?? new byte[0];
        }

        private void ParseHeader(byte[] body)
        {
            Dictionary<string, object> h = SkzRecFormat.AsMap(new MsgPackReader(body).ReadObject(), "header");
            if (!string.Equals(SkzRecFormat.Get(h, "format") as string, "skzrec", StringComparison.Ordinal))
            {
                throw new FormatException("not a .skzrec file");
            }
            long version = SkzRecFormat.AsLong(SkzRecFormat.Get(h, "version"), "version");
            if (version != 1)
            {
                throw SkzRecFormat.Bad("unsupported version " + version);
            }
            Header = h;
            Created = SkzRecFormat.Get(h, "created") as string;
            Profile = SkzRecFormat.Get(h, "profile") as string;
            Title = SkzRecFormat.Get(h, "title") as string;
            Meta = SkzRecFormat.Get(h, "meta") as Dictionary<string, object> ?? new Dictionary<string, object>(StringComparer.Ordinal);
            object chans = SkzRecFormat.Get(h, "channels");
            if (chans == null)
            {
                return;
            }
            object[] list = SkzRecFormat.AsArray(chans, "channels");
            for (int i = 0; i < list.Length; i++)
            {
                Dictionary<string, object> c = SkzRecFormat.AsMap(list[i], "channel");
                object[] fields = SkzRecFormat.Get(c, "fields") as object[] ?? new object[0];
                var names = new string[fields.Length];
                for (int k = 0; k < fields.Length; k++)
                {
                    names[k] = fields[k] as string;
                }
                _channels.Add(new SkzRecChannel
                {
                    Id = SkzRecFormat.AsInt(SkzRecFormat.Get(c, "id"), "channel id"),
                    Name = SkzRecFormat.Get(c, "name") as string,
                    Fields = names,
                });
            }
        }

        /// <summary>Trailer, footer and index chunks; false (nothing committed) if any of them is missing or bad.</summary>
        private bool TryLoadIndexed()
        {
            long footerLimit = _length - SkzRecFormat.TrailerLength;
            if (footerLimit < _headerEnd + SkzRecFormat.ChunkOverhead)
            {
                return false;
            }
            if (!ReadAt(footerLimit, _small, 0, SkzRecFormat.TrailerLength) || !SkzRecFormat.SameBytes(_small, 8, SkzRecFormat.EndMagic))
            {
                return false;
            }
            ulong rawOffset = SkzRecFormat.GetU32(_small, 0) | ((ulong)SkzRecFormat.GetU32(_small, 4) << 32);
            if (rawOffset < (ulong)_headerEnd || rawOffset > (ulong)(footerLimit - SkzRecFormat.ChunkOverhead))
            {
                return false;
            }
            long footerOffset = (long)rawOffset;
            RawChunk footer;
            if (!TryReadChunk(footerOffset, out footer) || footer.Type != SkzRecFormat.ChunkFooter || footer.End != footerLimit)
            {
                return false;
            }
            try
            {
                FooterInfo f = ParseFooter(footer.Body);
                var entries = new List<SkzRecIndexEntry>();
                for (int i = 0; i < f.IndexOffsets.Count; i++)
                {
                    long off = f.IndexOffsets[i];
                    RawChunk ic;
                    if (off < _headerEnd || !TryReadChunk(off, out ic) || ic.Type != SkzRecFormat.ChunkIndex || ic.End > footerOffset)
                    {
                        return false;
                    }
                    ParseIndex(ic.Body, entries);
                }
                if (entries.Count != f.Chunks)
                {
                    return false;
                }
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i].Offset < _headerEnd || entries[i].Offset > footerOffset - SkzRecFormat.ChunkOverhead)
                    {
                        return false;
                    }
                }
                _index = entries;
                _indexOffsets = f.IndexOffsets;
                ApplyFooter(f);
                ValidLength = _length;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>Sequential scan from the header up to the first truncated, CRC-bad or unparseable chunk.</summary>
        private void Scan()
        {
            var entries = new List<SkzRecIndexEntry>();
            var indexOffsets = new List<long>();
            long pos = _headerEnd;
            long validEnd = _headerEnd;
            RawChunk c;
            while (TryReadChunk(pos, out c))
            {
                if (c.Type == SkzRecFormat.ChunkData)
                {
                    SkzRecIndexEntry e = TryParseDataHead(c);
                    if (e == null)
                    {
                        break;
                    }
                    entries.Add(e);
                }
                else if (c.Type == SkzRecFormat.ChunkIndex)
                {
                    indexOffsets.Add(c.Offset);
                }
                else if (c.Type == SkzRecFormat.ChunkFooter)
                {
                    try
                    {
                        ApplyFooter(ParseFooter(c.Body));
                    }
                    catch (FormatException)
                    {
                        // A footer that does not parse is simply ignored.
                    }
                    break;
                }
                else if (c.Type == SkzRecFormat.ChunkHeader)
                {
                    break;
                }
                // Unknown chunk types with a good CRC are skipped (forward compatibility).
                validEnd = c.End;
                pos = c.End;
            }
            _index = entries;
            _indexOffsets = indexOffsets;
            ValidLength = validEnd;
        }

        private static SkzRecIndexEntry TryParseDataHead(RawChunk c)
        {
            try
            {
                var r = new MsgPackReader(c.Body);
                if (r.ReadArrayHeader() < 7)
                {
                    return null;
                }
                var e = new SkzRecIndexEntry();
                e.ChunkSeq = SkzRecFormat.AsInt(r.ReadInt64(), "chunkSeq");
                e.ChannelId = SkzRecFormat.AsInt(r.ReadInt64(), "channelId");
                e.UtFrom = r.ReadDouble();
                e.UtTo = r.ReadDouble();
                e.Count = SkzRecFormat.AsInt(r.ReadInt64(), "count");
                e.Offset = c.Offset;
                return e;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static void ParseIndex(byte[] body, List<SkzRecIndexEntry> into)
        {
            object[] list = SkzRecFormat.AsArray(new MsgPackReader(body).ReadObject(), "index");
            for (int i = 0; i < list.Length; i++)
            {
                object[] a = SkzRecFormat.AsArray(list[i], "index entry");
                if (a.Length < 6)
                {
                    throw SkzRecFormat.Bad("short index entry");
                }
                into.Add(new SkzRecIndexEntry
                {
                    ChunkSeq = SkzRecFormat.AsInt(a[0], "chunkSeq"),
                    ChannelId = SkzRecFormat.AsInt(a[1], "channelId"),
                    UtFrom = SkzRecFormat.AsDouble(a[2], "utFrom"),
                    UtTo = SkzRecFormat.AsDouble(a[3], "utTo"),
                    Count = SkzRecFormat.AsInt(a[4], "count"),
                    Offset = SkzRecFormat.AsLong(a[5], "fileOffset"),
                });
            }
        }

        private static FooterInfo ParseFooter(byte[] body)
        {
            Dictionary<string, object> m = SkzRecFormat.AsMap(new MsgPackReader(body).ReadObject(), "footer");
            var f = new FooterInfo();
            object offs = SkzRecFormat.Get(m, "indexOffsets");
            if (offs != null)
            {
                object[] a = SkzRecFormat.AsArray(offs, "indexOffsets");
                for (int i = 0; i < a.Length; i++)
                {
                    f.IndexOffsets.Add(SkzRecFormat.AsLong(a[i], "indexOffsets"));
                }
            }
            f.Chunks = SkzRecFormat.AsLong(SkzRecFormat.Get(m, "chunks"), "chunks");
            object marks = SkzRecFormat.Get(m, "marks");
            if (marks != null)
            {
                object[] a = SkzRecFormat.AsArray(marks, "marks");
                for (int i = 0; i < a.Length; i++)
                {
                    object[] mk = SkzRecFormat.AsArray(a[i], "mark");
                    if (mk.Length < 2)
                    {
                        throw SkzRecFormat.Bad("short mark");
                    }
                    f.Marks.Add(new SkzRecMark
                    {
                        Ut = SkzRecFormat.AsDouble(mk[0], "mark ut"),
                        Label = mk[1] as string,
                        Note = mk.Length > 2 ? mk[2] as string : null,
                    });
                }
            }
            f.Summary = SkzRecFormat.Get(m, "summary") as Dictionary<string, object>;
            f.Closed = SkzRecFormat.Get(m, "closed") as string;
            return f;
        }

        private void ApplyFooter(FooterInfo f)
        {
            _marks = f.Marks;
            Summary = f.Summary ?? new Dictionary<string, object>(StringComparer.Ordinal);
            Closed = f.Closed;
        }

        private bool TryReadChunk(long offset, out RawChunk chunk)
        {
            chunk = default(RawChunk);
            if (offset < 0 || _length - offset < SkzRecFormat.ChunkOverhead)
            {
                return false;
            }
            if (!ReadAt(offset, _small, 0, 5))
            {
                return false;
            }
            byte type = _small[0];
            uint len = SkzRecFormat.GetU32(_small, 1);
            if (len > _length - offset - SkzRecFormat.ChunkOverhead)
            {
                return false;
            }
            uint crc = Crc32.Update(Crc32.Initial, _small, 0, 5);
            var body = new byte[len];
            if (!ReadAt(offset + 5, body, 0, body.Length) || !ReadAt(offset + 5 + len, _small, 0, 4))
            {
                return false;
            }
            crc = Crc32.Finish(Crc32.Update(crc, body, 0, body.Length));
            if (crc != SkzRecFormat.GetU32(_small, 0))
            {
                return false;
            }
            chunk.Type = type;
            chunk.Body = body;
            chunk.Offset = offset;
            chunk.End = offset + SkzRecFormat.ChunkOverhead + len;
            return true;
        }

        private bool ReadAt(long position, byte[] buffer, int offset, int count)
        {
            _stream.Position = position;
            while (count > 0)
            {
                int n = _stream.Read(buffer, offset, count);
                if (n <= 0)
                {
                    return false;
                }
                offset += n;
                count -= n;
            }
            return true;
        }
    }

    /// <summary>Repairs an unclosed or truncated <c>.skzrec</c> file in place.</summary>
    public static class SkzRecRepair
    {
        /// <summary>
        /// Returns false and leaves a clean file untouched. Otherwise truncates the file to the reader's
        /// <c>ValidLength</c> and appends an index chunk covering every recovered data chunk, a footer (summary gets
        /// "repaired": true) and the trailer, then returns true. Throws FormatException if the file is not a
        /// .skzrec file or its header is unreadable, and IOException if it cannot be opened for writing (e.g. a
        /// writer still has it open).
        /// </summary>
        public static bool Repair(string path, string closed)
        {
            if (path == null)
            {
                throw new ArgumentNullException("path");
            }
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                IList<SkzRecIndexEntry> entries;
                IList<SkzRecMark> marks;
                Dictionary<string, object> summary;
                long records;
                long validLength;
                using (SkzRecReader r = SkzRecReader.Open(fs, true))
                {
                    if (!r.Repaired)
                    {
                        return false;
                    }
                    entries = r.Index;
                    marks = r.Marks;
                    summary = new Dictionary<string, object>(r.Summary, StringComparer.Ordinal);
                    records = r.RecordCount;
                    validLength = r.ValidLength;
                }
                summary["repaired"] = true;
                fs.SetLength(validLength);
                fs.Position = validLength;
                long pos = validLength;
                var w = new MsgPackWriter();
                var indexOffsets = new List<long>();
                if (entries.Count > 0)
                {
                    SkzRecFormat.WriteIndexBody(w, entries);
                    SkzRecFormat.WriteChunk(fs, SkzRecFormat.ChunkIndex, w.Buffer, w.Length, null, 0);
                    indexOffsets.Add(pos);
                    pos += SkzRecFormat.ChunkOverhead + w.Length;
                }
                var sum = new MsgPackWriter();
                sum.WriteValue(summary);
                w.Clear();
                SkzRecFormat.WriteFooterBody(w, indexOffsets, entries.Count, records, marks, sum, closed);
                long footerOffset = pos;
                SkzRecFormat.WriteChunk(fs, SkzRecFormat.ChunkFooter, w.Buffer, w.Length, null, 0);
                byte[] trailer = SkzRecFormat.Trailer(footerOffset);
                fs.Write(trailer, 0, trailer.Length);
                fs.Flush(true);
                return true;
            }
        }
    }
}
