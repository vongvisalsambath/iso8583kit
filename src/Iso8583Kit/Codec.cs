using System.Globalization;
using System.Text;

namespace Iso8583Kit;

/// <summary>The result of reading a message.</summary>
public sealed record UnpackResult(Iso8583Message? Message, IReadOnlyList<Iso8583Issue> Issues, IReadOnlyList<FieldSpan> Spans)
{
    /// <summary>True when the message was read with no errors.</summary>
    public bool IsValid => Message is not null && Issues.All(i => !i.IsError);
}

/// <summary>Packs and unpacks ISO 8583 messages.</summary>
public static class Iso8583Codec
{
    /// <summary>The largest message read or written, in bytes. A 1987 message with every field at its maximum is under 10 KB.</summary>
    public const int MaxBytes = 16 * 1024;

    // ---------------------------------------------------------------- unpacking

    /// <summary>
    /// Reads a message. Never throws for bad input: everything found is in <see cref="UnpackResult.Issues"/>, and
    /// <see cref="UnpackResult.Spans"/> says where each part was found.
    /// </summary>
    public static UnpackResult Unpack(ReadOnlySpan<byte> data, Iso8583Spec? spec = null, Iso8583Encoding? encoding = null)
    {
        spec ??= Iso8583Spec.Default1987;
        encoding ??= Iso8583Encoding.Ascii;
        var issues = new List<Iso8583Issue>();
        var spans = new List<FieldSpan>();
        UnpackResult Fail(string code, string message, int? field, int offset)
        {
            issues.Add(new(code, message, field, offset));
            return new UnpackResult(null, issues, spans);
        }
        if (data.Length > MaxBytes) return Fail("TOO-LONG", $"The message is {data.Length:N0} bytes; at most {MaxBytes:N0} are read.", null, 0);
        var pos = 0;

        // MTI
        var mtiLength = encoding.Bcd ? 2 : 4;
        if (data.Length < mtiLength) return Fail("TRUNCATED", "The message is too short to hold a message type indicator.", null, 0);
        var mti = encoding.Bcd ? FromBcd(data[..2], 4) : Ascii(data[..4]);
        if (mti is null || !Iso8583Message.MtiPattern().IsMatch(mti)) return Fail("MTI", $"The message type indicator {Show(mti ?? Hex(data[..mtiLength]))} isn't four digits.", null, 0);
        spans.Add(new FieldSpan(0, 0, mtiLength));
        pos = mtiLength;

        // bitmaps
        var present = new bool[129];
        var bitmaps = 1;
        for (var b = 0; b < bitmaps; b++)
        {
            var size = encoding.BinaryBitmap ? 8 : 16;
            if (data.Length - pos < size) return Fail("TRUNCATED", $"The message ends inside the {(b == 0 ? "primary" : "secondary")} bitmap.", 1, pos);
            byte[] bits;
            if (encoding.BinaryBitmap) bits = data.Slice(pos, 8).ToArray();
            else
            {
                var hex = Ascii(data.Slice(pos, 16));
                if (hex is null || !TryHex(hex, out bits)) return Fail("BITMAP", $"The {(b == 0 ? "primary" : "secondary")} bitmap {Show(hex ?? "")} isn't 16 hexadecimal characters.", 1, pos);
            }
            spans.Add(new FieldSpan(1, pos, size));
            pos += size;
            for (var i = 0; i < 64; i++)
                if ((bits[i / 8] & (0x80 >> (i % 8))) != 0) present[b * 64 + i + 1] = true;
            if (b == 0 && present[1]) bitmaps = 2;
        }
        if (present[65]) return Fail("BITMAP", "Bit 65 (a tertiary bitmap) is set; the 1987 version doesn't use it.", 65, pos);

        var message = new Iso8583Message(mti);
        for (var n = 2; n <= 128; n++)
        {
            if (!present[n]) continue;
            var def = spec[n];
            if (def is null) return Fail("UNDEFINED", $"Field {n} is present, but {spec.Name} doesn't define it, so its length is unknown and nothing after it can be read.", n, pos);
            var start = pos;
            // the length prefix
            int count;
            if (def.LengthType == LengthType.Fixed) count = def.Length;
            else
            {
                var digits = def.LengthType == LengthType.LLVar ? 2 : 3;
                var prefixBytes = encoding.Bcd ? (digits + 1) / 2 : digits;
                if (data.Length - pos < prefixBytes) return Fail("TRUNCATED", $"The message ends inside field {n}'s length.", n, pos);
                var prefix = encoding.Bcd ? FromBcd(data.Slice(pos, prefixBytes), digits) : Ascii(data.Slice(pos, prefixBytes));
                if (prefix is null || !int.TryParse(prefix, NumberStyles.None, CultureInfo.InvariantCulture, out count))
                    return Fail("LENGTH", $"Field {n}'s length {Show(prefix ?? Hex(data.Slice(pos, prefixBytes)))} isn't {digits} digits.", n, pos);
                if (count > def.Length) return Fail("LENGTH", $"Field {n} says it is {count} long; {def.Name} is at most {def.Length}.", n, pos);
                pos += prefixBytes;
            }
            // the value
            var bytes = ByteCount(def, count, encoding);
            if (data.Length - pos < bytes) return Fail("TRUNCATED", $"The message ends inside field {n} ({def.Name}): it needs {bytes} bytes, {data.Length - pos} are left.", n, pos);
            var raw = data.Slice(pos, bytes);
            var value = Decode(def, raw, count, encoding);
            if (value is null) return Fail("FORMAT", $"Field {n} ({def.Name}) isn't valid {Describe(def, encoding)}.", n, pos);
            pos += bytes;
            spans.Add(new FieldSpan(n, start, pos - start));
            message[n] = value;
            var problem = Rules.CheckFormat(def, value);
            if (problem is not null) issues.Add(new("FORMAT", $"Field {n} ({def.Name}): {problem}", n, start));
        }
        if (present[1] && !Enumerable.Range(65, 64).Any(i => present[i]))
            issues.Add(new("BITMAP", "The secondary bitmap is present but no field from 65 to 128 is.", 1, null, IsError: false));
        if (pos < data.Length) issues.Add(new("EXTRA", $"{data.Length - pos} byte{(data.Length - pos == 1 ? "" : "s")} follow the last field.", null, pos));
        return new UnpackResult(message, issues, spans);
    }

    /// <summary>Reads a message written as hexadecimal (spaces and line breaks allowed), as logs and tools usually show it.</summary>
    public static UnpackResult UnpackHex(string hex, Iso8583Spec? spec = null, Iso8583Encoding? encoding = null)
    {
        var compact = new string((hex ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (compact.Length > MaxBytes * 2)
            return new UnpackResult(null, [new Iso8583Issue("TOO-LONG", $"That is more than {MaxBytes:N0} bytes.")], []);
        if (!TryHex(compact, out var bytes))
            return new UnpackResult(null, [new Iso8583Issue("HEX", "That isn't hexadecimal: use pairs of 0-9 and A-F.")], []);
        return Unpack(bytes, spec, encoding);
    }

    private static int ByteCount(FieldDefinition def, int count, Iso8583Encoding e) => def.Format switch
    {
        FieldFormat.B when !e.Bcd => count * 2, // text layouts carry binary as two hexadecimal characters per byte
        FieldFormat.B => count,
        FieldFormat.N or FieldFormat.Z when e.Bcd => (count + 1) / 2,
        _ => count,
    };

    private static string Describe(FieldDefinition def, Iso8583Encoding e) => def.Format switch
    {
        FieldFormat.N or FieldFormat.Z when e.Bcd => "packed BCD",
        FieldFormat.B when !e.Bcd => "hexadecimal text",
        FieldFormat.B => "binary data",
        _ => "ASCII text",
    };

    private static string? Decode(FieldDefinition def, ReadOnlySpan<byte> raw, int count, Iso8583Encoding e)
    {
        switch (def.Format)
        {
            case FieldFormat.B when !e.Bcd:
                var text = Ascii(raw);
                return text is not null && TryHex(text, out _) ? text.ToUpperInvariant() : null;
            case FieldFormat.B:
                return Hex(raw);
            case FieldFormat.N when e.Bcd:
                return FromBcd(raw, count);
            case FieldFormat.Z when e.Bcd:
                // track 2 in BCD: the separator is the nibble D, padded at the end with F
                var hex = Hex(raw);
                return hex.Length >= count ? hex[..count].Replace('D', '=') : null;
            default:
                return Ascii(raw);
        }
    }

    // ---------------------------------------------------------------- packing

    /// <summary>Writes a message. Throws <see cref="Iso8583Exception"/> when a field is missing from the spec, too long or in the wrong format.</summary>
    public static byte[] Pack(Iso8583Message message, Iso8583Spec? spec = null, Iso8583Encoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        spec ??= Iso8583Spec.Default1987;
        encoding ??= Iso8583Encoding.Ascii;
        var problems = new List<Iso8583Issue>();
        if (!Iso8583Message.MtiPattern().IsMatch(message.Mti ?? "")) problems.Add(new("MTI", "The message type indicator must be four digits."));
        foreach (var (n, value) in message.Fields)
        {
            var def = spec[n];
            if (def is null) { problems.Add(new("UNDEFINED", $"{spec.Name} doesn't define field {n}.", n)); continue; }
            var count = def.Format == FieldFormat.B ? value.Length / 2 : value.Length;
            if (def.Format == FieldFormat.B && (value.Length % 2 != 0 || !TryHex(value, out _))) { problems.Add(new("FORMAT", $"Field {n} is binary: give it as hexadecimal pairs.", n)); continue; }
            if (def.LengthType == LengthType.Fixed ? count != def.Length : count > def.Length || count == 0)
                problems.Add(new("LENGTH", def.LengthType == LengthType.Fixed
                    ? $"Field {n} ({def.Name}) is exactly {def.Length} long, not {count}."
                    : $"Field {n} ({def.Name}) is 1 to {def.Length} long, not {count}.", n));
            if (Rules.CheckFormat(def, value) is string p) problems.Add(new("FORMAT", $"Field {n} ({def.Name}): {p}", n));
        }
        if (problems.Count > 0) throw new Iso8583Exception(problems);

        var output = new List<byte>(256);
        output.AddRange(encoding.Bcd ? ToBcd(message.Mti!, 4) : Encoding.ASCII.GetBytes(message.Mti!));
        var bits = new byte[16];
        var secondary = message.Fields.Any(f => f.Key > 64);
        if (secondary) bits[0] |= 0x80;
        foreach (var (n, _) in message.Fields) bits[(n - 1) / 8] |= (byte)(0x80 >> ((n - 1) % 8));
        var bitmapBytes = bits.AsSpan(0, secondary ? 16 : 8).ToArray();
        output.AddRange(encoding.BinaryBitmap
            ? bitmapBytes
            : Encoding.ASCII.GetBytes(Convert.ToHexString(bitmapBytes[..8]) + (secondary ? Convert.ToHexString(bitmapBytes[8..]) : "")));

        foreach (var (n, value) in message.Fields)
        {
            var def = spec[n]!;
            var count = def.Format == FieldFormat.B ? value.Length / 2 : value.Length;
            if (def.LengthType != LengthType.Fixed)
            {
                var digits = def.LengthType == LengthType.LLVar ? 2 : 3;
                var prefix = count.ToString(new string('0', digits), CultureInfo.InvariantCulture);
                output.AddRange(encoding.Bcd ? ToBcd(prefix, digits) : Encoding.ASCII.GetBytes(prefix));
            }
            output.AddRange(def.Format switch
            {
                FieldFormat.B when !encoding.Bcd => Encoding.ASCII.GetBytes(value.ToUpperInvariant()),
                FieldFormat.B => Convert.FromHexString(value),
                FieldFormat.N when encoding.Bcd => ToBcd(value, value.Length),
                FieldFormat.Z when encoding.Bcd => Convert.FromHexString(value.Replace('=', 'D') + (value.Length % 2 == 1 ? "F" : "")),
                _ => Encoding.ASCII.GetBytes(value),
            });
        }
        if (output.Count > MaxBytes) throw new Iso8583Exception([new("TOO-LONG", $"The message would be {output.Count:N0} bytes; the limit is {MaxBytes:N0}.")]);
        return output.ToArray();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Printable ASCII only; null otherwise.</summary>
    private static string? Ascii(ReadOnlySpan<byte> b)
    {
        foreach (var x in b) if (x is < 0x20 or > 0x7E) return null;
        return Encoding.ASCII.GetString(b);
    }

    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);

    internal static bool TryHex(string s, out byte[] bytes)
    {
        bytes = [];
        if (s.Length % 2 != 0) return false;
        foreach (var c in s) if (!char.IsAsciiHexDigit(c)) return false;
        bytes = Convert.FromHexString(s);
        return true;
    }

    /// <summary>Packed BCD: <paramref name="digits"/> digits, padded on the left with 0 to fill whole bytes. Null if a nibble isn't a digit.</summary>
    private static string? FromBcd(ReadOnlySpan<byte> b, int digits)
    {
        var hex = Convert.ToHexString(b);
        foreach (var c in hex) if (!char.IsAsciiDigit(c)) return null;
        var pad = hex.Length - digits;
        if (pad < 0 || hex[..pad].Any(c => c != '0')) return null;
        return hex[pad..];
    }

    private static byte[] ToBcd(string digits, int count) => Convert.FromHexString(count % 2 == 1 ? "0" + digits : digits);

    /// <summary>Text from a message, quoted for a message: at most 40 characters, nothing unprintable.</summary>
    internal static string Show(string s) => "\"" + new string(s.Take(40).Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray()) + (s.Length > 40 ? "…" : "") + "\"";
}
