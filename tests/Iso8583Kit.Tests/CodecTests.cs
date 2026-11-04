using System.Text;
using Xunit;

namespace Iso8583Kit.Tests;

public sealed class CodecTests
{
    internal const string Visa = "4111111111111111";   // the networks' public test card numbers
    internal const string Mastercard = "5555555555554444";

    public static IEnumerable<object[]> Encodings => [[Iso8583Encoding.Ascii], [Iso8583Encoding.AsciiBinaryBitmap], [Iso8583Encoding.BcdBinary]];

    internal static Iso8583Message Purchase()
    {
        var m = new Iso8583Message("0200");
        m[2] = Visa;
        m[3] = "000000";
        m[4] = "000000002500";
        m[7] = "1104093015";
        m[11] = "000123";
        m[12] = "163015";
        m[13] = "1104";
        m[14] = "2812";
        m[22] = "051";
        m[25] = "00";
        m[35] = Visa + "=28121010000012300000";
        m[37] = "430809123456";
        m[41] = "TERM0001";
        m[42] = "DEMO MERCHANT01";
        m[43] = "DEMO COFFEE".PadRight(23) + "PHNOM PENH".PadRight(13) + "12KH"; // name 23, city 13, region 2, country 2
        m[49] = "840";
        m[52] = "0123456789ABCDEF";
        m[55] = "9F2608A1B2C3D4E5F607089F2701809F10120110A00003220000000000000000000000FF950500000000009A032611049C01009F02060000000025005F2A02084082021900";
        return m;
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public void PackedMessagesReadBackUnchanged(Iso8583Encoding encoding)
    {
        var m = Purchase();
        m[90] = new string('0', 42);
        m[102] = "001234567890";
        var bytes = Iso8583Codec.Pack(m, encoding: encoding);
        var r = Iso8583Codec.Unpack(bytes, encoding: encoding);
        Assert.True(r.IsValid, string.Join("; ", r.Issues));
        Assert.Equal("0200", r.Message!.Mti);
        Assert.Equal(m.Fields, r.Message.Fields);
        Assert.Equal(new[] { 0, 1, 1 }.Concat(m.Fields.Select(f => f.Key)), r.Spans.Select(s => s.Field)); // two bitmaps: field 90 needs the secondary
        Assert.Equal(bytes.Length, r.Spans[^1].Offset + r.Spans[^1].Length);
    }

    [Fact]
    public void AnAsciiMessageIsAllPrintableText()
    {
        var bytes = Iso8583Codec.Pack(Purchase());
        Assert.All(bytes, b => Assert.InRange(b, (byte)0x20, (byte)0x7E));
        Assert.Contains("0123456789ABCDEF", Encoding.ASCII.GetString(bytes)); // the PIN block, as hexadecimal text
        var odd = Encoding.ASCII.GetBytes("0200" + "0000000000001000" + "0123456789ABCDEZ");
        Assert.Contains("FORMAT", Iso8583Codec.Unpack(odd).Issues.Select(i => i.Code));
    }

    [Fact]
    public void TheEchoTestHasItsKnownBitmaps()
    {
        var m = new Iso8583Message("0800") { [7] = "1104093015", [11] = "000001", [70] = "301" };
        var ascii = Encoding.ASCII.GetString(Iso8583Codec.Pack(m));
        Assert.Equal("0800" + "8220000000000000" + "0400000000000000" + "1104093015" + "000001" + "301", ascii);
        var bcd = Convert.ToHexString(Iso8583Codec.Pack(m, encoding: Iso8583Encoding.BcdBinary));
        Assert.Equal("0800" + "8220000000000000" + "0400000000000000" + "1104093015" + "000001" + "0301", bcd);
    }

    [Fact]
    public void BcdPacksDigitsAndTrack2()
    {
        var m = new Iso8583Message("0100") { [2] = "4111111111111", [35] = "4111111111111=2812101" };
        var hex = Convert.ToHexString(Iso8583Codec.Pack(m, encoding: Iso8583Encoding.BcdBinary));
        // PAN: length 13 as BCD 13, digits padded on the left; track 2: length 21, = as D, padded with F
        Assert.Contains("13" + "04111111111111", hex);
        Assert.Contains("21" + "4111111111111D2812101F", hex);
        Assert.Equal(m.Fields, Iso8583Codec.Unpack(Convert.FromHexString(hex), encoding: Iso8583Encoding.BcdBinary).Message!.Fields);
    }

    [Theory]
    [InlineData("02", "TRUNCATED")]
    [InlineData("02X0", "MTI")]
    [InlineData("0200ZZ00000000000000", "BITMAP")]
    [InlineData("0200400000", "TRUNCATED")]
    [InlineData("02004000000000000000", "TRUNCATED")]
    [InlineData("020040000000000000001", "TRUNCATED")]
    [InlineData("02004000000000000000XX4111", "LENGTH")]
    [InlineData("02004000000000000000204111111111111111111111", "LENGTH")]
    [InlineData("0200400000000000000016411111111111111199", "EXTRA")]
    [InlineData("0200" + "8000000000000000" + "0000000020000000" + "ABC", "UNDEFINED")]
    [InlineData("02008000000000000000800000000000000000000", "BITMAP")]
    [InlineData("0200400000000000000016411111111111111A", "FORMAT")]
    public void BrokenMessagesAreReportedNotThrown(string ascii, string code)
    {
        var r = Iso8583Codec.Unpack(Encoding.ASCII.GetBytes(ascii));
        Assert.False(r.IsValid);
        Assert.Contains(code, r.Issues.Select(i => i.Code));
    }

    [Fact]
    public void ControlBytesInTextFieldsAreRefused()
    {
        var bytes = Encoding.ASCII.GetBytes("02000000000000800000TE\u001B[2JMM").ToArray(); // field 41, 8 bytes
        var r = Iso8583Codec.Unpack(bytes);
        Assert.Contains("FORMAT", r.Issues.Select(i => i.Code));
        Assert.All(r.Issues, i => Assert.DoesNotContain('\u001B', i.Message));
    }

    [Fact]
    public void PackingRefusesWhatCantBeWritten()
    {
        Assert.Throws<Iso8583Exception>(() => Iso8583Codec.Pack(new Iso8583Message("200")));
        Assert.Contains("exactly 6 long", Assert.Throws<Iso8583Exception>(() => Iso8583Codec.Pack(new Iso8583Message("0200") { [3] = "00" })).Message);
        Assert.Contains("digits only", Assert.Throws<Iso8583Exception>(() => Iso8583Codec.Pack(new Iso8583Message("0200") { [4] = "00000000002X" })).Message);
        Assert.Contains("1 to 19 long", Assert.Throws<Iso8583Exception>(() => Iso8583Codec.Pack(new Iso8583Message("0200") { [2] = new string('4', 20) })).Message);
        Assert.Contains("hexadecimal", Assert.Throws<Iso8583Exception>(() => Iso8583Codec.Pack(new Iso8583Message("0200") { [52] = "XYZ" })).Message);
        Assert.Contains("doesn't define field 99", Assert.Throws<Iso8583Exception>(() => Iso8583Codec.Pack(new Iso8583Message("0200") { [99] = "1" })).Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Iso8583Message("0200")[1] = "x");
        Assert.Throws<ArgumentOutOfRangeException>(() => new Iso8583Message("0200")[129] = "x");
    }

    [Fact]
    public void HexInputIsAccepted_AndOversizedInputRefused()
    {
        var bytes = Iso8583Codec.Pack(Purchase(), encoding: Iso8583Encoding.BcdBinary);
        var spaced = string.Join(" ", Convert.ToHexString(bytes).Chunk(8).Select(c => new string(c)));
        Assert.True(Iso8583Codec.UnpackHex(spaced, encoding: Iso8583Encoding.BcdBinary).IsValid);
        Assert.Contains("HEX", Iso8583Codec.UnpackHex("0200XYZ").Issues.Select(i => i.Code));
        Assert.Contains("TOO-LONG", Iso8583Codec.Unpack(new byte[Iso8583Codec.MaxBytes + 1]).Issues.Select(i => i.Code));
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public void TwentyThousandDamagedMessagesNeverThrowOrLeakTheCardNumber(Iso8583Encoding encoding)
    {
        var good = Iso8583Codec.Pack(Purchase(), encoding: encoding);
        var random = new Random(8583);
        var slowest = 0L;
        for (var i = 0; i < 20_000; i++)
        {
            var b = good.ToList();
            for (var e = 0; e < 1 + random.Next(4); e++)
            {
                var at = random.Next(b.Count + 1);
                switch (random.Next(4))
                {
                    case 0 when b.Count > 0: b[Math.Min(at, b.Count - 1)] = (byte)random.Next(256); break;
                    case 1: b.Insert(at, (byte)random.Next(256)); break;
                    case 2 when at < b.Count: b.RemoveRange(at, Math.Min(1 + random.Next(4), b.Count - at)); break;
                    default: b = b.Take(at).ToList(); break;
                }
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = Iso8583Codec.Unpack(b.ToArray(), encoding: encoding);
            if (r.Message is not null)
            {
                _ = Rules.Check(r.Message);
                foreach (var (n, v) in r.Message.Fields) _ = Explain.Field(n, v, r.Message);
            }
            slowest = Math.Max(slowest, sw.ElapsedMilliseconds);
            foreach (var issue in r.Issues.Concat(r.Message is null ? [] : Rules.Check(r.Message)))
            {
                Assert.DoesNotContain(Visa, issue.Message);
                Assert.DoesNotContain(issue.Message, c => char.IsControl(c));
            }
        }
        Assert.True(slowest < 200, $"slowest {slowest} ms");
    }
}
