using System;
using System.Globalization;
using System.Text;
using Skizzy.Protocol.Json;
using Xunit;

namespace Skizzy.Protocol.Tests
{
    public class JsonTests
    {
        private static string Fmt(double v, int digits = NumberFormat.DoubleDigits)
        {
            var b = new ByteBuffer();
            NumberFormat.WriteDouble(b, v, digits);
            return b.ToUtf8String();
        }

        [Theory]
        [InlineData(0.0, "0")]
        [InlineData(1.0, "1")]
        [InlineData(-42.0, "-42")]
        [InlineData(0.1, "0.1")]
        [InlineData(1203.4, "1203.4")]
        [InlineData(3281937421.0, "3281937421")]
        [InlineData(204511.32, "204511.32")]
        [InlineData(1e-7, "1e-7")]
        [InlineData(0.000123, "0.000123")]
        [InlineData(1e20, "1e+20")]
        [InlineData(-2.5e-12, "-2.5e-12")]
        [InlineData(123456789.123456, "123456789.123456")]
        [InlineData(99999.99999999999, "100000")]
        public void Double_formats_cleanly(double v, string expected)
        {
            Assert.Equal(expected, Fmt(v));
        }

        [Fact]
        public void Negative_zero_is_zero()
        {
            Assert.Equal("0", Fmt(-0.0));
        }

        [Fact]
        public void Single_uses_seven_digits()
        {
            var b = new ByteBuffer();
            NumberFormat.WriteSingle(b, 0.1f);
            Assert.Equal("0.1", b.ToUtf8String());
            b.Clear();
            NumberFormat.WriteSingle(b, 0.3f);
            Assert.Equal("0.3", b.ToUtf8String());
            b.Clear();
            NumberFormat.WriteSingle(b, 1234.567f);
            Assert.Equal("1234.567", b.ToUtf8String());
        }

        [Fact]
        public void Double_round_trips_within_15_digits()
        {
            var rnd = new Random(12345);
            for (int i = 0; i < 20000; i++)
            {
                double mant = rnd.NextDouble() * 2 - 1;
                int exp = rnd.Next(-30, 30);
                double v = mant * Math.Pow(10, exp);
                string s = Fmt(v);
                double back = double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
                double tol = Math.Abs(v) * 1e-14 + 1e-300;
                Assert.True(Math.Abs(back - v) <= tol, $"{v:R} -> {s} -> {back:R}");
            }
        }

        [Fact]
        public void Longs_are_exact()
        {
            foreach (long v in new[] { 0L, 1L, -1L, long.MaxValue, long.MinValue, 4294967295L, 1234567890123L })
            {
                var b = new ByteBuffer();
                NumberFormat.WriteLong(b, v);
                Assert.Equal(v.ToString(CultureInfo.InvariantCulture), b.ToUtf8String());
            }
        }

        [Fact]
        public void Parse_and_write_round_trip()
        {
            string src = "{\"a\":1,\"b\":[true,false,null,\"x\\n\\\"y\"],\"c\":{\"d\":-0.5,\"e\":\"\\u00e9\\ud83d\\ude00\"},\"big\":3281937421}";
            var v = JsonParser.Parse(src);
            Assert.Equal(1, v.GetInt("a"));
            Assert.Equal(3281937421L, v.GetLong("big"));
            Assert.Equal("x\n\"y", v.GetArr("b")[3].AsString());
            Assert.Equal("\u00e9\U0001F600", v.GetObj("c").GetString("e"));
            string again = v.ToString();
            Assert.True(JsonValue.DeepEquals(v, JsonParser.Parse(again)));
            Assert.Equal("{\"a\":1,\"b\":[true,false,null,\"x\\n\\\"y\"],\"c\":{\"d\":-0.5,\"e\":\"\u00e9\U0001F600\"},\"big\":3281937421}", again);
        }

        [Theory]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("{\"a\":}")]
        [InlineData("[1,]")]
        [InlineData("01")]
        [InlineData("1.")]
        [InlineData("\"\\x\"")]
        [InlineData("tru")]
        [InlineData("{\"a\":1} x")]
        [InlineData("\"a\u0001\"")]
        public void Invalid_json_throws(string src)
        {
            Assert.Throws<JsonParseException>(() => JsonParser.Parse(src));
        }

        [Fact]
        public void Depth_limit_is_enforced()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 70; i++) sb.Append('[');
            for (int i = 0; i < 70; i++) sb.Append(']');
            Assert.Throws<JsonParseException>(() => JsonParser.Parse(sb.ToString()));
            var ok = new StringBuilder();
            for (int i = 0; i < 64; i++) ok.Append('[');
            for (int i = 0; i < 64; i++) ok.Append(']');
            JsonParser.Parse(ok.ToString());
        }

        [Fact]
        public void Invalid_utf8_is_rejected()
        {
            var bad = new byte[] { (byte)'"', 0xC3, 0x28, (byte)'"' };
            Assert.Throws<JsonParseException>(() => JsonParser.Parse(bad, 0, bad.Length));
            var overlong = new byte[] { (byte)'"', 0xC0, 0xAF, (byte)'"' };
            Assert.Throws<JsonParseException>(() => JsonParser.Parse(overlong, 0, overlong.Length));
            Assert.False(JsonParser.IsValidUtf8(bad, 1, 2));
            var good = Encoding.UTF8.GetBytes("h\u00e9llo \U0001F600");
            Assert.True(JsonParser.IsValidUtf8(good, 0, good.Length));
        }

        [Fact]
        public void NonFinite_numbers_use_sibling_flag()
        {
            var b = new ByteBuffer();
            var w = new JsonWriter(b);
            w.BeginObject().Prop("x", double.NaN).Prop("y", 1.5).Prop("z", double.NegativeInfinity).EndObject();
            Assert.Equal("{\"x\":null,\"x$nonFinite\":\"NaN\",\"y\":1.5,\"z\":null,\"z$nonFinite\":\"-Inf\"}", b.ToUtf8String());
            var o = JsonValue.Obj().SetFinite("v", double.PositiveInfinity);
            Assert.Equal("{\"v\":null,\"v$nonFinite\":\"+Inf\"}", o.ToString());
        }

        [Fact]
        public void Base64_matches_framework()
        {
            var rnd = new Random(7);
            for (int len = 0; len < 40; len++)
            {
                var data = new byte[len];
                rnd.NextBytes(data);
                var b = new ByteBuffer();
                new JsonWriter(b).Base64(data, 0, len);
                Assert.Equal("\"" + Convert.ToBase64String(data) + "\"", b.ToUtf8String());
            }
        }

        [Fact]
        public void Control_characters_are_escaped()
        {
            var s = JsonValue.Str("a\u0001b\tc").ToString();
            Assert.Equal("\"a\\u0001b\\tc\"", s);
        }

        [Fact]
        public void Pretty_output_is_valid_json()
        {
            var v = JsonParser.Parse("{\"a\":[1,2,{\"b\":null}],\"c\":{}}");
            var pretty = v.ToPrettyString();
            Assert.Contains("\n", pretty);
            Assert.True(JsonValue.DeepEquals(v, JsonParser.Parse(pretty)));
        }

        [Fact]
        public void Object_keeps_insertion_order_and_indexes_large_objects()
        {
            var o = JsonValue.Obj();
            for (int i = 0; i < 50; i++) o.Set("k" + i, i);
            Assert.Equal(49, o.GetInt("k49"));
            Assert.Equal("k0", o.KeyAt(0));
            o.Set("k10", "x");
            Assert.Equal("x", o.GetString("k10"));
            Assert.True(o.Remove("k0"));
            Assert.Null(o.Get("k0"));
            Assert.Equal(49, o.GetInt("k49"));
        }
    }
}
