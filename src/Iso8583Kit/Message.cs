using System.Text.RegularExpressions;

namespace Iso8583Kit;

/// <summary>An ISO 8583 message: its message type indicator and its fields. Binary fields hold uppercase hexadecimal.</summary>
public sealed partial class Iso8583Message
{
    private readonly SortedDictionary<int, string> fields = [];

    /// <summary>Creates a message with a message type indicator such as "0200".</summary>
    public Iso8583Message(string mti)
    {
        Mti = mti;
    }

    /// <summary>The message type indicator: four digits, such as 0200 (financial request) or 0810 (network management response).</summary>
    public string Mti { get; set; }

    /// <summary>A field's value, or null if absent. Setting null removes the field.</summary>
    public string? this[int number]
    {
        get => fields.TryGetValue(number, out var v) ? v : null;
        set
        {
            if (number is < 2 or > 128) throw new ArgumentOutOfRangeException(nameof(number), "Fields are numbered 2 to 128.");
            if (value is null) fields.Remove(number);
            else fields[number] = value;
        }
    }

    /// <summary>The fields present, in order.</summary>
    public IReadOnlyCollection<KeyValuePair<int, string>> Fields => fields;

    /// <summary>A copy of the message, for building a response from a request.</summary>
    public Iso8583Message Clone(string? mti = null)
    {
        var m = new Iso8583Message(mti ?? Mti);
        foreach (var (k, v) in fields) m.fields[k] = v;
        return m;
    }

    [GeneratedRegex("^[0-9]{4}\\z")]
    internal static partial Regex MtiPattern();
}

/// <summary>How a message is laid out in bytes. Variants differ between networks and hosts; these cover the common ones.</summary>
/// <param name="Name">A name for messages.</param>
/// <param name="BinaryBitmap">Bitmaps as 8 raw bytes; otherwise as 16 hexadecimal characters.</param>
/// <param name="Bcd">MTI, numeric fields, track 2 and length prefixes as packed BCD, and binary fields as raw bytes; otherwise everything is ASCII text, with binary fields as hexadecimal (two characters per byte).</param>
public sealed record Iso8583Encoding(string Name, bool BinaryBitmap, bool Bcd)
{
    /// <summary>Everything as ASCII text, the bitmap in hexadecimal. Common in test tools and some hosts.</summary>
    public static Iso8583Encoding Ascii { get; } = new("ASCII", false, false);

    /// <summary>ASCII fields with a binary bitmap.</summary>
    public static Iso8583Encoding AsciiBinaryBitmap { get; } = new("ASCII with a binary bitmap", true, false);

    /// <summary>Packed BCD numbers and lengths, binary bitmap, ASCII text: the compact layout many terminals and hosts use.</summary>
    public static Iso8583Encoding BcdBinary { get; } = new("BCD with a binary bitmap", true, true);
}

/// <summary>A problem in a message.</summary>
public sealed record Iso8583Issue(string Code, string Message, int? Field = null, int? Offset = null, bool IsError = true);

/// <summary>Where a part of the message is in the bytes.</summary>
/// <param name="Field">The field number; 0 for the MTI, 1 for the bitmaps.</param>
/// <param name="Offset">Where it starts, in bytes from the start of the message.</param>
/// <param name="Length">How many bytes it takes, length prefix included.</param>
public sealed record FieldSpan(int Field, int Offset, int Length);

/// <summary>Thrown when a message can't be packed. <see cref="Problems"/> says why.</summary>
public sealed class Iso8583Exception(IReadOnlyList<Iso8583Issue> problems)
    : Exception(string.Join(" ", problems.Select(p => p.Message)))
{
    /// <summary>Every reason.</summary>
    public IReadOnlyList<Iso8583Issue> Problems { get; } = problems;
}
