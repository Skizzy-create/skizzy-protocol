using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Skizzy.Host.Core.Rec;
using Xunit;

namespace Skizzy.Host.Tests
{
    public class RecFileTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "skzrec-tests-" + Guid.NewGuid().ToString("N"));

        public RecFileTests()
        {
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // ---------------------------------------------------------------- CRC-32

        [Fact]
        public void Crc32_of_check_string_is_standard_value()
        {
            byte[] b = Encoding.ASCII.GetBytes("123456789");
            Assert.Equal(0xCBF43926u, Crc32.Compute(b, 0, b.Length));
            uint running = Crc32.Update(Crc32.Initial, b, 0, 4);
            running = Crc32.Update(running, b, 4, 5);
            Assert.Equal(0xCBF43926u, Crc32.Finish(running));
            Assert.Equal(0u, Crc32.Compute(b, 0, 0));
        }

        // ---------------------------------------------------------------- MessagePack

        [Theory]
        [InlineData(0L, 1)]
        [InlineData(127L, 1)]
        [InlineData(128L, 2)]
        [InlineData(255L, 2)]
        [InlineData(256L, 3)]
        [InlineData(65535L, 3)]
        [InlineData(65536L, 5)]
        [InlineData((long)int.MaxValue, 5)]
        [InlineData(4294967295L, 5)]
        [InlineData(4294967296L, 9)]
        [InlineData(long.MaxValue, 9)]
        [InlineData(-1L, 1)]
        [InlineData(-32L, 1)]
        [InlineData(-33L, 2)]
        [InlineData(-128L, 2)]
        [InlineData(-129L, 3)]
        [InlineData(-32768L, 3)]
        [InlineData(-32769L, 5)]
        [InlineData((long)int.MinValue, 5)]
        [InlineData((long)int.MinValue - 1, 9)]
        [InlineData(long.MinValue, 9)]
        public void MsgPack_int_roundtrips_at_every_encoding_boundary(long value, int encodedLength)
        {
            var w = new MsgPackWriter();
            w.WriteInt(value);
            Assert.Equal(encodedLength, w.Length);
            byte[] bytes = w.ToArray();

            var r = new MsgPackReader(bytes);
            Assert.Equal(MsgPackType.Int, r.PeekType());
            Assert.Equal(value, r.ReadInt64());
            Assert.True(r.End);
            Assert.Equal(value, new MsgPackReader(bytes).ReadObject());
            Assert.Equal((double)value, new MsgPackReader(bytes).ReadDouble());

            w.Clear();
            w.WriteValue(value);
            Assert.Equal(bytes, w.ToArray());
        }

        [Fact]
        public void MsgPack_all_clr_integer_types_and_uint64()
        {
            var w = new MsgPackWriter();
            w.WriteValue((byte)200);
            w.WriteValue((sbyte)-5);
            w.WriteValue((short)-300);
            w.WriteValue((ushort)60000);
            w.WriteValue(7);
            w.WriteValue(4000000000u);
            w.WriteValue(8UL);
            w.WriteValue(ulong.MaxValue);
            w.WriteUInt(ulong.MaxValue);
            var r = new MsgPackReader(w.ToArray());
            Assert.Equal(200L, r.ReadInt64());
            Assert.Equal(-5L, r.ReadInt64());
            Assert.Equal(-300L, r.ReadInt64());
            Assert.Equal(60000L, r.ReadInt64());
            Assert.Equal(7L, r.ReadObject());
            Assert.Equal(4000000000L, r.ReadInt64());
            Assert.Equal(8L, r.ReadObject());
            Assert.Throws<FormatException>(() => r.ReadInt64()); // does not fit a long; position unchanged
            Assert.Equal(ulong.MaxValue, r.ReadObject());
            Assert.Equal((double)ulong.MaxValue, r.ReadDouble());
            Assert.True(r.End);
        }

        [Fact]
        public void MsgPack_nil_bool_double_float_roundtrip()
        {
            var w = new MsgPackWriter();
            w.WriteNil();
            w.WriteBool(true);
            w.WriteBool(false);
            w.WriteDouble(Math.PI);
            w.WriteDouble(double.MaxValue);
            w.WriteDouble(double.NegativeInfinity);
            w.WriteDouble(double.NaN);
            int before = w.Length;
            w.WriteFloat(1.25f);
            Assert.Equal(5, w.Length - before);
            w.WriteFloat(float.MinValue);
            w.WriteFloat(-3.5e-20f);
            w.WriteString(null);
            w.WriteValue(null);
            w.WriteValue(true);
            w.WriteValue(2.5);
            w.WriteValue(0.5f);

            var r = new MsgPackReader(w.ToArray());
            Assert.Equal(MsgPackType.Nil, r.PeekType());
            Assert.True(r.TryReadNil());
            Assert.False(r.TryReadNil());
            Assert.Equal(MsgPackType.Bool, r.PeekType());
            Assert.True(r.ReadBool());
            Assert.False(r.ReadBool());
            Assert.Equal(MsgPackType.Float, r.PeekType());
            Assert.Equal(Math.PI, r.ReadDouble());
            Assert.Equal(double.MaxValue, r.ReadDouble());
            Assert.Equal(double.NegativeInfinity, r.ReadDouble());
            Assert.True(double.IsNaN(r.ReadDouble()));
            Assert.Equal(1.25, r.ReadDouble());
            Assert.Equal((double)float.MinValue, r.ReadDouble());
            Assert.Equal((double)-3.5e-20f, r.ReadDouble());
            Assert.Null(r.ReadString());
            Assert.Null(r.ReadObject());
            Assert.True((bool)r.ReadObject());
            Assert.Equal(2.5, r.ReadObject());
            Assert.Equal(0.5, r.ReadObject()); // float32 widened to double
            Assert.True(r.End);

            w.Clear();
            w.WriteDouble(-7.9);
            w.WriteFloat(3.99f);
            var r2 = new MsgPackReader(w.ToArray());
            Assert.Equal(-7L, r2.ReadInt64());
            Assert.Equal(3L, r2.ReadInt64());
        }

        [Theory]
        [InlineData(0, 0xa0)]
        [InlineData(31, 0xbf)]
        [InlineData(32, 0xd9)]
        [InlineData(255, 0xd9)]
        [InlineData(256, 0xda)]
        [InlineData(70000, 0xdb)]
        public void MsgPack_string_roundtrips_at_every_length_boundary(int length, int headerByte)
        {
            string ascii = new string('a', length);
            var w = new MsgPackWriter();
            w.WriteString(ascii);
            Assert.Equal(headerByte, (int)w.Buffer[0]);
            Assert.Equal(ascii, new MsgPackReader(w.ToArray()).ReadString());

            string mixed = Mixed(length);
            w.Clear();
            w.WriteString(mixed);
            var r = new MsgPackReader(w.Buffer, 0, w.Length);
            Assert.Equal(MsgPackType.String, r.PeekType());
            Assert.Equal(mixed, r.ReadString());
            Assert.True(r.End);
            Assert.Equal(mixed, new MsgPackReader(w.Buffer, 0, w.Length).ReadObject());
        }

        [Fact]
        public void MsgPack_utf8_byte_length_picks_the_header()
        {
            var w = new MsgPackWriter();
            string s31 = new string('é', 15) + "a"; // 31 UTF-8 bytes
            string s32 = new string('é', 16); // 32 UTF-8 bytes
            string emoji = "\U0001F680 skizzy ✓ 日本";
            w.WriteString(s31);
            Assert.Equal(0xbf, (int)w.Buffer[0]);
            int at = w.Length;
            w.WriteString(s32);
            Assert.Equal(0xd9, (int)w.Buffer[at]);
            w.WriteString(emoji);
            var r = new MsgPackReader(w.ToArray());
            Assert.Equal(s31, r.ReadString());
            Assert.Equal(s32, r.ReadString());
            Assert.Equal(emoji, r.ReadString());
        }

        [Theory]
        [InlineData(0, 0xc4)]
        [InlineData(255, 0xc4)]
        [InlineData(256, 0xc5)]
        [InlineData(65535, 0xc5)]
        [InlineData(65536, 0xc6)]
        public void MsgPack_bin_roundtrips(int length, int headerByte)
        {
            var data = new byte[length + 2];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i * 7);
            }
            var w = new MsgPackWriter();
            w.WriteBin(data, 1, length);
            w.WriteValue(new byte[] { 9, 8, 7 });
            Assert.Equal(headerByte, (int)w.Buffer[0]);
            var r = new MsgPackReader(w.ToArray());
            Assert.Equal(MsgPackType.Bin, r.PeekType());
            Assert.Equal(data.Skip(1).Take(length).ToArray(), r.ReadBin());
            Assert.Equal(new byte[] { 9, 8, 7 }, r.ReadObject());
            Assert.True(r.End);
        }

        [Fact]
        public void MsgPack_nested_arrays_and_maps_roundtrip_and_skip()
        {
            var many = new Dictionary<string, object>();
            for (int i = 0; i < 20; i++)
            {
                many["k" + i] = i * 1000;
            }
            var big = new object[40];
            for (int i = 0; i < big.Length; i++)
            {
                big[i] = i % 2 == 0 ? (object)("s" + i) : i;
            }
            var value = new Dictionary<string, object>
            {
                ["name"] = "Kirov",
                ["pos"] = new object[] { 1.5, -2.25, 1000L },
                ["tags"] = new object[] { "a", null, true, new object[] { new object[0], new Dictionary<string, object>() } },
                ["blob"] = new byte[] { 1, 2, 3 },
                ["inner"] = new Dictionary<string, object> { ["x"] = 1, ["y"] = new List<object> { 2L, "z", -0.5f } },
                ["big"] = big,
                ["many"] = many,
            };
            var w = new MsgPackWriter();
            w.WriteValue(value);
            w.WriteValue(new object[] { value, value });
            w.WriteInt(42);
            byte[] bytes = w.ToArray();

            var r = new MsgPackReader(bytes);
            Assert.Equal(MsgPackType.Map, r.PeekType());
            AssertDeep(value, r.ReadObject());
            Assert.Equal(MsgPackType.Array, r.PeekType());
            object[] pair = Assert.IsType<object[]>(r.ReadObject());
            Assert.Equal(2, pair.Length);
            AssertDeep(value, pair[1]);
            Assert.Equal(42L, r.ReadInt64());
            Assert.True(r.End);

            r = new MsgPackReader(bytes);
            r.Skip();
            r.Skip();
            Assert.Equal(42L, r.ReadInt64());
            Assert.True(r.End);

            // Headers read one by one.
            r = new MsgPackReader(bytes);
            Assert.Equal(value.Count, r.ReadMapHeader());
        }

        [Fact]
        public void MsgPack_skip_ext_and_non_string_map_keys()
        {
            // fixext1, ext8 (len 2), then int 7.
            byte[] ext = { 0xd4, 0x01, 0xaa, 0xc7, 0x02, 0x05, 0x01, 0x02, 0x07 };
            var r = new MsgPackReader(ext);
            Assert.Equal(MsgPackType.Ext, r.PeekType());
            Assert.Throws<FormatException>(() => r.ReadObject());
            r.Skip();
            Assert.Equal(MsgPackType.Ext, r.PeekType());
            r.Skip();
            Assert.Equal(7L, r.ReadInt64());
            Assert.True(r.End);

            var w = new MsgPackWriter();
            w.WriteMapHeader(2);
            w.WriteInt(5);
            w.WriteString("five");
            w.WriteDouble(1.5);
            w.WriteBool(true);
            var m = Assert.IsType<Dictionary<string, object>>(new MsgPackReader(w.ToArray()).ReadObject());
            Assert.Equal("five", m["5"]);
            Assert.True((bool)m["1.5"]);
        }

        [Fact]
        public void MsgPack_truncated_or_invalid_input_throws_FormatException()
        {
            var w = new MsgPackWriter();
            w.WriteValue(new object[]
            {
                1L, 300L, 70000L, 5000000000L, -100L, -40000L, -3000000000L, 1.5, 2.5f, "hello",
                new string('x', 40), new string('y', 300), new byte[] { 1, 2, 3 }, new byte[300],
                new Dictionary<string, object> { ["k"] = new object[] { true, null } }, new object[20],
            });
            byte[] full = w.ToArray();
            for (int len = 0; len < full.Length; len++)
            {
                Assert.Throws<FormatException>(() => new MsgPackReader(full, 0, len).ReadObject());
                Assert.Throws<FormatException>(() => new MsgPackReader(full, 0, len).Skip());
            }
            Assert.Throws<FormatException>(() => new MsgPackReader(new byte[0]).PeekType());
            Assert.Throws<FormatException>(() => new MsgPackReader(new byte[] { 0xcb, 0, 0 }).ReadDouble());
            Assert.Throws<FormatException>(() => new MsgPackReader(new byte[] { 0xd3, 0, 0 }).ReadInt64());
            Assert.Throws<FormatException>(() => new MsgPackReader(new byte[] { 0xd9 }).ReadString());
            Assert.Throws<FormatException>(() => new MsgPackReader(new byte[] { 0xa5, 0x61 }).ReadString());
            Assert.Throws<FormatException>(() => new MsgPackReader(new byte[] { 0xc6, 0, 0, 0, 9, 1 }).ReadBin());
            Assert.Throws<FormatException>(() => new MsgPackReader(new byte[] { 0xdd, 0xff, 0xff, 0xff, 0xff }).ReadArrayHeader());
            Assert.Throws<FormatException>(() => new MsgPackReader(new byte[] { 0xdf, 0, 0, 0, 3, 1 }).ReadMapHeader());
            Assert.Throws<FormatException>(() => new MsgPackReader(new byte[] { 0xc1 }).ReadObject());
            Assert.Throws<FormatException>(() => new MsgPackReader(new byte[] { 0xc1 }).Skip());
            Assert.Throws<FormatException>(() => new MsgPackReader(new byte[] { 0xa1, 0x61 }).ReadInt64());
            Assert.Throws<FormatException>(() => new MsgPackReader(new byte[] { 0x01 }).ReadString());
            Assert.Throws<FormatException>(() => new MsgPackReader(new byte[] { 0x01 }).ReadBool());
        }

        // ---------------------------------------------------------------- .skzrec writer/reader

        [Fact]
        public void Writer_and_reader_roundtrip_two_channels()
        {
            string path = NewPath("roundtrip");
            var meta = new Dictionary<string, object> { ["ksp"] = "1.12.5", ["seed"] = 7 };
            using (SkzRecWriter w = SkzRecWriter.Create(path, "2026-11-11T10:00:00Z", "naval", "Test run", Channels(), meta))
            {
                for (int i = 0; i < 100; i++)
                {
                    double ut = 100 + i * 0.5;
                    WriteUnit(w, i, ut);
                    if (i % 10 == 0)
                    {
                        WriteEvent(w, i, ut);
                    }
                }
                w.Mark(120, "contact", "first radar contact");
                w.Mark(130, "end", null);
                w.Close(new Dictionary<string, object> { ["kills"] = 3, ["winner"] = "blue" }, "2026-11-11T10:05:00Z");
                Assert.False(w.Failed);
                Assert.Null(w.Error);
                Assert.Equal(110L, w.Records);
                Assert.Equal(new FileInfo(path).Length, w.BytesWritten);
                w.Close(null, "ignored"); // idempotent
            }

            using (SkzRecReader r = SkzRecReader.Open(path))
            {
                Assert.False(r.Repaired);
                Assert.Equal(new FileInfo(path).Length, r.ValidLength);
                Assert.Equal("2026-11-11T10:00:00Z", r.Created);
                Assert.Equal("naval", r.Profile);
                Assert.Equal("Test run", r.Title);
                Assert.Equal("skzrec", r.Header["format"]);
                Assert.Equal("1.12.5", r.Meta["ksp"]);
                Assert.Equal(7L, r.Meta["seed"]);
                Assert.Equal(2, r.Channels.Count);
                SkzRecChannel units = r.Channel("units");
                Assert.Equal(1, units.Id);
                Assert.Equal(new[] { "id", "lat", "lon", "alt" }, units.Fields);
                Assert.Equal(2, r.Channel("events").Id);
                Assert.Equal(new[] { "kind", "text" }, r.Channel("events").Fields);
                Assert.Null(r.Channel("nope"));
                Assert.Equal(110L, r.RecordCount);
                Assert.Equal(100.0, r.UtFrom);
                Assert.Equal(149.5, r.UtTo);

                List<SkzRecRecord> all = r.Records(1, double.NegativeInfinity, double.PositiveInfinity).ToList();
                Assert.Equal(100, all.Count);
                for (int i = 0; i < all.Count; i++)
                {
                    AssertUnit(all[i], i, 100 + i * 0.5);
                }

                List<SkzRecRecord> window = r.Records(1, 110, 120).ToList();
                Assert.Equal(21, window.Count);
                Assert.Equal(110.0, window[0].Ut);
                Assert.Equal(120.0, window[window.Count - 1].Ut);
                Assert.All(window, x => Assert.InRange(x.Ut, 110.0, 120.0));
                Assert.Empty(r.Records(1, 200, 300));

                List<SkzRecRecord> events = r.Records(2).ToList();
                Assert.Equal(10, events.Count);
                Assert.Equal(125.0, events[5].Ut);
                Assert.Equal("miss", events[5].Fields[0]);
                Assert.Equal("salvo 50", events[5].Fields[1]);

                Assert.Equal(2, r.Marks.Count);
                Assert.Equal(120.0, r.Marks[0].Ut);
                Assert.Equal("contact", r.Marks[0].Label);
                Assert.Equal("first radar contact", r.Marks[0].Note);
                Assert.Equal("end", r.Marks[1].Label);
                Assert.Null(r.Marks[1].Note);
                Assert.Equal(3L, r.Summary["kills"]);
                Assert.Equal("blue", r.Summary["winner"]);
                Assert.Equal("2026-11-11T10:05:00Z", r.Closed);
            }
        }

        [Fact]
        public void Writer_over_memory_stream_and_cancelled_record()
        {
            var ms = new MemoryStream();
            using (var w = new SkzRecWriter(ms, "c", "p", "t", Channels(), null))
            {
                MsgPackWriter m = w.BeginRecord(1, 1.0, 4);
                m.WriteInt(1);
                w.CancelRecord(); // caller gave up half way
                WriteUnit(w, 2, 2.0);
                Assert.Equal(1L, w.Records);
            }
            using (SkzRecReader r = SkzRecReader.Open(new MemoryStream(ms.ToArray())))
            {
                Assert.False(r.Repaired);
                Assert.Empty(r.Summary);
                Assert.Empty(r.Marks);
                Assert.Empty(r.Meta);
                Assert.Null(r.Closed);
                SkzRecRecord only = Assert.Single(r.Records(1));
                AssertUnit(only, 2, 2.0);
                Assert.Empty(r.Records(2));
            }
        }

        [Fact]
        public void Writer_rejects_misuse()
        {
            string path = NewPath("misuse");
            using (SkzRecWriter w = SkzRecWriter.Create(path, "c", "p", "t", Channels(), null))
            {
                Assert.Throws<ArgumentException>(() => w.BeginRecord(99, 0, 0));
                w.BeginRecord(1, 0, 0);
                Assert.Throws<InvalidOperationException>(() => w.BeginRecord(1, 0, 0));
                Assert.Throws<InvalidOperationException>(() => w.Flush());
                w.EndRecord();
                Assert.Throws<InvalidOperationException>(() => w.EndRecord());
                Assert.Throws<ArgumentException>(() => w.Close(new Dictionary<string, object> { ["bad"] = new object() }, "x"));
                Assert.False(w.IsClosed);
            }
            // CreateNew refuses to overwrite.
            Assert.Throws<IOException>(() => SkzRecWriter.Create(path, "c", "p", "t", Channels(), null));
            var dupes = new List<SkzRecChannel> { new SkzRecChannel { Id = 1, Name = "a" }, new SkzRecChannel { Id = 1, Name = "b" } };
            Assert.Throws<ArgumentException>(() => new SkzRecWriter(new MemoryStream(), "c", "p", "t", dupes, null));
        }

        [Fact]
        public void Chunks_close_by_time_span()
        {
            string path = NewPath("span");
            int written;
            using (SkzRecWriter w = SkzRecWriter.Create(path, "c", "p", "t", Channels(), null))
            {
                for (int i = 0; i <= 300; i++)
                {
                    WriteUnit(w, i, i * 0.1);
                    WriteEvent(w, i, i * 0.1);
                }
                w.Close(null, "done");
                written = w.Chunks;
            }
            using (SkzRecReader r = SkzRecReader.Open(path))
            {
                Assert.False(r.Repaired);
                Assert.Equal(written, r.Index.Count);
                Assert.Equal(Enumerable.Range(0, written), r.Index.Select(e => e.ChunkSeq));
                foreach (int ch in new[] { 1, 2 })
                {
                    List<SkzRecIndexEntry> entries = r.Index.Where(e => e.ChannelId == ch).ToList();
                    Assert.InRange(entries.Count, 5, 8);
                    for (int k = 0; k < entries.Count; k++)
                    {
                        Assert.InRange(entries[k].UtTo - entries[k].UtFrom, 0.0, 5.2);
                        if (k < entries.Count - 1)
                        {
                            Assert.InRange(entries[k].UtTo - entries[k].UtFrom, 4.999, 5.2);
                            Assert.InRange(entries[k + 1].UtFrom, entries[k].UtTo, double.MaxValue);
                        }
                    }
                }
                Assert.Equal(602L, r.RecordCount);
                Assert.Equal(0.0, r.UtFrom);
                Assert.Equal(300 * 0.1, r.UtTo);

                List<SkzRecRecord> units = r.Records(1).ToList();
                Assert.Equal(301, units.Count);
                for (int i = 0; i < units.Count; i++)
                {
                    AssertUnit(units[i], i, i * 0.1);
                }

                // A window across a chunk boundary.
                int expected = Enumerable.Range(0, 301).Count(i => i * 0.1 >= 4.0 && i * 0.1 <= 12.0);
                List<SkzRecRecord> window = r.Records(2, 4.0, 12.0).ToList();
                Assert.Equal(expected, window.Count);
                Assert.All(window, x => Assert.InRange(x.Ut, 4.0, 12.0));
            }
        }

        [Fact]
        public void Chunks_close_by_size_and_index_chunks_repeat_every_64()
        {
            string path = NewPath("size");
            int chunks;
            using (SkzRecWriter w = SkzRecWriter.Create(path, "c", "p", "t", Channels(), null))
            {
                w.MaxChunkBytes = 512;
                w.MaxChunkSpanS = 1e9;
                for (int i = 0; i < 2000; i++)
                {
                    WriteUnit(w, i, i * 0.01);
                }
                w.Close(null, null);
                chunks = w.Chunks;
            }
            Assert.InRange(chunks, 129, 1000);
            using (SkzRecReader r = SkzRecReader.Open(path))
            {
                Assert.False(r.Repaired);
                Assert.Equal(chunks, r.Index.Count);
                Assert.Equal((chunks + 63) / 64, r.IndexOffsets.Count);
                Assert.InRange(r.IndexOffsets.Count, 2, int.MaxValue);
                Assert.All(r.Index, e => Assert.InRange(e.Count, 1, 20));
                Assert.Equal(2000L, r.RecordCount);
                for (int k = 1; k < r.Index.Count; k++)
                {
                    Assert.InRange(r.Index[k].Offset, r.Index[k - 1].Offset + 1, long.MaxValue);
                }

                List<SkzRecRecord> all = r.Records(1).ToList();
                Assert.Equal(2000, all.Count);
                for (int i = 0; i < all.Count; i++)
                {
                    AssertUnit(all[i], i, i * 0.01);
                }

                int expected = Enumerable.Range(0, 2000).Count(i => i * 0.01 >= 5.0 && i * 0.01 <= 6.0);
                Assert.Equal(expected, r.Records(1, 5.0, 6.0).Count());
            }
        }

        // ---------------------------------------------------------------- crash safety and repair

        [Fact]
        public void Unclosed_file_is_recovered_and_repaired()
        {
            string path = NewPath("live");
            string crash = NewPath("crash");
            long flushedRecords;
            using (SkzRecWriter w = SkzRecWriter.Create(path, "c", "p", "t", Channels(), null))
            {
                for (int i = 0; i < 120; i++)
                {
                    WriteUnit(w, i, i * 0.1);
                    if (i % 10 == 0)
                    {
                        WriteEvent(w, i, i * 0.1);
                    }
                }
                w.Mark(3.0, "only-in-footer", null);
                w.Flush();
                for (int i = 120; i < 150; i++)
                {
                    WriteUnit(w, i, i * 0.1);
                }
                w.Flush();
                flushedRecords = w.Records;
                for (int i = 150; i < 160; i++)
                {
                    WriteUnit(w, i, i * 0.1); // still buffered when the "crash" happens
                }

                // A file still being written can be opened.
                using (SkzRecReader live = SkzRecReader.Open(path))
                {
                    Assert.True(live.Repaired);
                    Assert.Equal(flushedRecords, live.RecordCount);
                }
                File.WriteAllBytes(crash, ReadShared(path));
            }
            Assert.Equal(162L, flushedRecords);

            List<double> recovered;
            using (SkzRecReader r = SkzRecReader.Open(crash))
            {
                Assert.True(r.Repaired);
                Assert.Equal(new FileInfo(crash).Length, r.ValidLength);
                Assert.Equal(flushedRecords, r.RecordCount);
                Assert.Empty(r.Marks);
                Assert.Empty(r.Summary);
                Assert.Equal("t", r.Title);
                recovered = r.Records(1).Select(x => x.Ut).ToList();
                Assert.Equal(150, recovered.Count);
                Assert.Equal(12, r.Records(2).Count());
            }

            Assert.True(SkzRecRepair.Repair(crash, "repaired-at"));
            using (SkzRecReader r = SkzRecReader.Open(crash))
            {
                Assert.False(r.Repaired);
                Assert.Equal(flushedRecords, r.RecordCount);
                Assert.Equal(recovered, r.Records(1).Select(x => x.Ut).ToList());
                Assert.Equal(12, r.Records(2).Count());
                Assert.True((bool)r.Summary["repaired"]);
                Assert.Equal("repaired-at", r.Closed);
                Assert.Single(r.IndexOffsets);
            }
            Assert.False(SkzRecRepair.Repair(crash, "again"));

            // The original was closed normally: clean, and Repair leaves it byte-for-byte untouched.
            byte[] before = File.ReadAllBytes(path);
            Assert.False(SkzRecRepair.Repair(path, "x"));
            Assert.Equal(before, File.ReadAllBytes(path));
            using (SkzRecReader r = SkzRecReader.Open(path))
            {
                Assert.False(r.Repaired);
                Assert.Equal(172L, r.RecordCount);
                Assert.Single(r.Marks);
            }
        }

        [Fact]
        public void Truncated_closed_file_loses_only_the_torn_chunk()
        {
            string path = NewPath("torn");
            WriteSpanFile(path);
            long total;
            long tornOffset;
            int tornCount;
            using (SkzRecReader r = SkzRecReader.Open(path))
            {
                Assert.False(r.Repaired);
                SkzRecIndexEntry last = r.Index[r.Index.Count - 1];
                total = r.RecordCount;
                tornOffset = last.Offset;
                tornCount = last.Count;
            }
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                fs.SetLength(tornOffset + 20); // mid-chunk; index, footer and trailer gone
            }

            int recovered;
            using (SkzRecReader r = SkzRecReader.Open(path))
            {
                Assert.True(r.Repaired);
                Assert.Equal(tornOffset, r.ValidLength);
                Assert.Equal(total - tornCount, r.RecordCount);
                recovered = r.Records(1).Count() + r.Records(2).Count();
                Assert.Equal(total - tornCount, recovered);
                Assert.Empty(r.Marks);
            }

            Assert.True(SkzRecRepair.Repair(path, "fixed"));
            AssertIndexFooterTrailerAfter(path, tornOffset);
            using (SkzRecReader r = SkzRecReader.Open(path))
            {
                Assert.False(r.Repaired);
                Assert.Equal(total - tornCount, r.RecordCount);
                Assert.Equal(recovered, r.Records(1).Count() + r.Records(2).Count());
            }
        }

        [Fact]
        public void Corrupt_footer_crc_falls_back_to_scan_and_keeps_all_data()
        {
            string path = NewPath("badfooter");
            WriteSpanFile(path);
            long total;
            using (SkzRecReader r = SkzRecReader.Open(path))
            {
                total = r.RecordCount;
            }
            byte[] bytes = File.ReadAllBytes(path);
            bytes[bytes.Length - 20] ^= 0xff; // inside the footer body/crc
            File.WriteAllBytes(path, bytes);

            using (SkzRecReader r = SkzRecReader.Open(path))
            {
                Assert.True(r.Repaired);
                Assert.Equal(total, r.RecordCount);
                Assert.Empty(r.Marks); // footer unreadable
            }
            Assert.True(SkzRecRepair.Repair(path, "fixed"));
            using (SkzRecReader r = SkzRecReader.Open(path))
            {
                Assert.False(r.Repaired);
                Assert.Equal(total, r.RecordCount);
            }
        }

        [Fact]
        public void Missing_trailer_keeps_marks_from_a_valid_footer()
        {
            string path = NewPath("notrailer");
            WriteSpanFile(path);
            long total;
            using (SkzRecReader r = SkzRecReader.Open(path))
            {
                total = r.RecordCount;
            }
            long len = new FileInfo(path).Length;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                fs.SetLength(len - 16);
            }
            using (SkzRecReader r = SkzRecReader.Open(path))
            {
                Assert.True(r.Repaired);
                Assert.Equal(total, r.RecordCount);
                Assert.Equal("span-mark", Assert.Single(r.Marks).Label);
                Assert.Equal("v", r.Summary["k"]);
            }
            Assert.True(SkzRecRepair.Repair(path, "fixed"));
            using (SkzRecReader r = SkzRecReader.Open(path))
            {
                Assert.False(r.Repaired);
                Assert.Equal(total, r.RecordCount);
                Assert.Equal("span-mark", Assert.Single(r.Marks).Label);
                Assert.Equal("v", r.Summary["k"]);
                Assert.True((bool)r.Summary["repaired"]);
            }
        }

        // ---------------------------------------------------------------- disk full

        [Fact]
        public void Disk_full_sets_Failed_and_later_calls_do_not_throw()
        {
            var s = new FailingStream(4096);
            var w = new SkzRecWriter(s, "c", "p", "t", Channels(), null);
            Assert.False(w.Failed);
            for (int i = 0; i < 5000 && !w.Failed; i++)
            {
                WriteUnit(w, i, i * 0.1);
                if (i % 20 == 0)
                {
                    w.Flush();
                }
            }
            Assert.True(w.Failed);
            Assert.Contains("disk full", w.Error);
            Assert.True(s.Disposed);
            Assert.InRange(w.BytesWritten, 8, s.Written);
            long bytes = w.BytesWritten;
            long records = w.Records;

            MsgPackWriter m = w.BeginRecord(1, 1.0, 4);
            m.WriteInt(1);
            m.WriteDouble(2);
            m.WriteDouble(3);
            m.WriteDouble(4);
            w.EndRecord();
            w.BeginRecord(1, 2.0, 0);
            w.BeginRecord(1, 3.0, 0);
            w.BeginRecord(99, 1.0, 0);
            w.EndRecord();
            w.EndRecord();
            w.CancelRecord();
            w.Mark(1, "x", "y");
            w.Flush();
            w.Close(new Dictionary<string, object> { ["bad"] = new object() }, "closed");
            w.Close(null, null);
            w.Dispose();
            Assert.Equal(bytes, w.BytesWritten);
            Assert.Equal(records, w.Records);
            Assert.True(w.Failed);
        }

        [Fact]
        public void Disk_full_while_writing_the_header()
        {
            var s = new FailingStream(10);
            var w = new SkzRecWriter(s, "c", "p", "t", Channels(), null);
            Assert.True(w.Failed);
            Assert.Contains("disk full", w.Error);
            Assert.Equal(8L, w.BytesWritten);
            WriteUnit(w, 1, 1.0);
            w.Mark(1, "x", null);
            w.Flush();
            w.Dispose();
            Assert.Equal(0, w.Chunks);
        }

        // ---------------------------------------------------------------- not a recording

        [Fact]
        public void Not_a_skzrec_file_throws_FormatException()
        {
            string path = NewPath("junk");
            File.WriteAllText(path, "this is not a recording, just some text\n");
            FormatException ex = Assert.Throws<FormatException>(() => SkzRecReader.Open(path));
            Assert.Contains("not a .skzrec file", ex.Message);
            Assert.Throws<FormatException>(() => SkzRecRepair.Repair(path, "x"));

            File.WriteAllBytes(path, new byte[0]);
            Assert.Throws<FormatException>(() => SkzRecReader.Open(path));
            Assert.Throws<FormatException>(() => SkzRecReader.Open(new MemoryStream(Encoding.ASCII.GetBytes("SKZREC1\n"))));
            File.Delete(path); // the reader released the file
        }

        // ---------------------------------------------------------------- helpers

        private string NewPath(string name)
        {
            return Path.Combine(_dir, name + ".skzrec");
        }

        private static List<SkzRecChannel> Channels()
        {
            return new List<SkzRecChannel>
            {
                new SkzRecChannel { Id = 1, Name = "units", Fields = new[] { "id", "lat", "lon", "alt" } },
                new SkzRecChannel { Id = 2, Name = "events", Fields = new[] { "kind", "text" } },
            };
        }

        private static void WriteUnit(SkzRecWriter w, int i, double ut)
        {
            MsgPackWriter m = w.BeginRecord(1, ut, 4);
            m.WriteInt(i);
            m.WriteDouble(-0.5 * i);
            m.WriteDouble(0.25 * i);
            m.WriteDouble(1000.0 + i);
            w.EndRecord();
        }

        private static void WriteEvent(SkzRecWriter w, int i, double ut)
        {
            MsgPackWriter m = w.BeginRecord(2, ut, 2);
            m.WriteString(i % 20 == 10 ? "miss" : "hit");
            m.WriteString("salvo " + i);
            w.EndRecord();
        }

        private static void AssertUnit(SkzRecRecord rec, int i, double ut)
        {
            Assert.Equal(ut, rec.Ut);
            Assert.Equal(4, rec.Fields.Length);
            Assert.Equal((long)i, rec.Fields[0]);
            Assert.Equal(-0.5 * i, rec.Fields[1]);
            Assert.Equal(0.25 * i, rec.Fields[2]);
            Assert.Equal(1000.0 + i, rec.Fields[3]);
        }

        /// <summary>Closed file with records 0.1 s apart over 30 s on both channels, one mark and a summary.</summary>
        private static void WriteSpanFile(string path)
        {
            using (SkzRecWriter w = SkzRecWriter.Create(path, "c", "p", "t", Channels(), null))
            {
                for (int i = 0; i <= 300; i++)
                {
                    WriteUnit(w, i, i * 0.1);
                    if (i % 3 == 0)
                    {
                        WriteEvent(w, i, i * 0.1);
                    }
                }
                w.Mark(10, "span-mark", "note");
                w.Close(new Dictionary<string, object> { ["k"] = "v" }, "closed");
                Assert.False(w.Failed);
            }
        }

        /// <summary>After <paramref name="validLength"/> the file holds exactly one index chunk, one footer chunk and the trailer.</summary>
        private static void AssertIndexFooterTrailerAfter(string path, long validLength)
        {
            byte[] b = File.ReadAllBytes(path);
            long pos = validLength;
            int chunks = 0;
            while (pos + 9 <= b.Length - 16)
            {
                long len = BitConverter.ToUInt32(b, (int)pos + 1);
                pos += 9 + len;
                chunks++;
            }
            Assert.Equal(2, chunks); // one index chunk, one footer chunk
            Assert.Equal(b.Length - 16, pos);
            Assert.Equal(3, (int)b[validLength]);
            Assert.Equal("SKZEND1\n", Encoding.ASCII.GetString(b, b.Length - 8, 8));
        }

        private static byte[] ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static string Mixed(int n)
        {
            const string pattern = "aé日Ω";
            var sb = new StringBuilder(n);
            for (int i = 0; i < n; i++)
            {
                sb.Append(pattern[i % pattern.Length]);
            }
            return sb.ToString();
        }

        private static void AssertDeep(object expected, object actual)
        {
            if (expected == null)
            {
                Assert.Null(actual);
                return;
            }
            if (expected is byte[] eb)
            {
                Assert.Equal(eb, Assert.IsType<byte[]>(actual));
                return;
            }
            if (expected is IDictionary<string, object> ed)
            {
                var ad = Assert.IsType<Dictionary<string, object>>(actual);
                Assert.Equal(ed.Count, ad.Count);
                foreach (KeyValuePair<string, object> kv in ed)
                {
                    object av;
                    Assert.True(ad.TryGetValue(kv.Key, out av), "missing key " + kv.Key);
                    AssertDeep(kv.Value, av);
                }
                return;
            }
            if (expected is IList<object> el)
            {
                var aa = Assert.IsType<object[]>(actual);
                Assert.Equal(el.Count, aa.Length);
                for (int i = 0; i < aa.Length; i++)
                {
                    AssertDeep(el[i], aa[i]);
                }
                return;
            }
            if (expected is int ei)
            {
                Assert.Equal((long)ei, Assert.IsType<long>(actual));
                return;
            }
            if (expected is float ef)
            {
                Assert.Equal((double)ef, Assert.IsType<double>(actual));
                return;
            }
            Assert.Equal(expected, actual);
        }

        /// <summary>Accepts writes until more than <c>limit</c> bytes would have been written, then throws "disk full".</summary>
        private sealed class FailingStream : Stream
        {
            private readonly long _limit;

            public FailingStream(long limit)
            {
                _limit = limit;
            }

            public long Written { get; private set; }

            public bool Disposed { get; private set; }

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Written + count > _limit)
                {
                    throw new IOException("disk full");
                }
                Written += count;
            }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }
    }
}
