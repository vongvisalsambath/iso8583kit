namespace Iso8583Kit;

/// <summary>What a field may contain.</summary>
public enum FieldFormat
{
    /// <summary>Digits only.</summary>
    N,
    /// <summary>Letters and digits.</summary>
    AN,
    /// <summary>Letters, digits and special characters (printable ASCII).</summary>
    ANS,
    /// <summary>Binary data, shown as hexadecimal.</summary>
    B,
    /// <summary>Track 2 data: digits and the separators = and D.</summary>
    Z,
    /// <summary>An amount with a C (credit) or D (debit) sign, then digits.</summary>
    XN,
}

/// <summary>How a field's length is given.</summary>
public enum LengthType
{
    /// <summary>Always <see cref="FieldDefinition.Length"/> long.</summary>
    Fixed,
    /// <summary>A 2-digit length prefix, then up to <see cref="FieldDefinition.Length"/>.</summary>
    LLVar,
    /// <summary>A 3-digit length prefix, then up to <see cref="FieldDefinition.Length"/>.</summary>
    LLLVar,
}

/// <summary>How much harm a field's value could do if shown or logged.</summary>
public enum Sensitivity
{
    /// <summary>Safe to show.</summary>
    None,
    /// <summary>A card number: shown as the first 6 and last 4 digits only.</summary>
    Pan,
    /// <summary>Never shown: track data, PIN blocks, card verification data, security information.</summary>
    Secret,
}

/// <summary>One data element. <see cref="Length"/> counts digits for N and Z fields, bytes for B fields, characters otherwise.</summary>
public sealed record FieldDefinition(int Number, string Name, FieldFormat Format, int Length, LengthType LengthType = LengthType.Fixed, Sensitivity Sensitivity = Sensitivity.None);

/// <summary>A set of field definitions: which number means what.</summary>
public sealed class Iso8583Spec
{
    private readonly FieldDefinition?[] fields = new FieldDefinition?[129];

    /// <summary>A name for messages, such as "ISO 8583:1987".</summary>
    public string Name { get; }

    /// <summary>Creates a spec from definitions for fields 2 to 128.</summary>
    public Iso8583Spec(string name, IEnumerable<FieldDefinition> definitions)
    {
        Name = name;
        foreach (var d in definitions)
        {
            if (d.Number is < 2 or > 128) throw new ArgumentOutOfRangeException(nameof(definitions), $"Field {d.Number}: fields are numbered 2 to 128 (1 is the secondary bitmap).");
            if (d.Length is < 1 or > 999 || (d.LengthType == LengthType.LLVar && d.Length > 99))
                throw new ArgumentOutOfRangeException(nameof(definitions), $"Field {d.Number}: the length {d.Length} doesn't fit its length type.");
            fields[d.Number] = d;
        }
    }

    /// <summary>The definition of a field, or null if this spec doesn't define it.</summary>
    public FieldDefinition? this[int number] => number is >= 2 and <= 128 ? fields[number] : null;

    /// <summary>Every defined field, in order.</summary>
    public IEnumerable<FieldDefinition> Fields => fields.OfType<FieldDefinition>();

    /// <summary>
    /// The 1987 version's data elements as they are commonly used. Fields 46 to 63 and 111 to 127 are reserved for
    /// national and private use and defined here as generic LLLVAR text; networks give them their own meaning.
    /// </summary>
    public static Iso8583Spec Default1987 { get; } = new("ISO 8583:1987", Build1987());

    private static IEnumerable<FieldDefinition> Build1987()
    {
        FieldDefinition F(int n, string name, FieldFormat f, int len, LengthType t = LengthType.Fixed, Sensitivity s = Sensitivity.None) => new(n, name, f, len, t, s);
        const FieldFormat N = FieldFormat.N, AN = FieldFormat.AN, ANS = FieldFormat.ANS, B = FieldFormat.B, Z = FieldFormat.Z, XN = FieldFormat.XN;
        const LengthType LL = LengthType.LLVar, LLL = LengthType.LLLVar;
        var list = new List<FieldDefinition>
        {
            F(2, "Primary account number (PAN)", N, 19, LL, Sensitivity.Pan),
            F(3, "Processing code", N, 6),
            F(4, "Amount, transaction", N, 12),
            F(5, "Amount, settlement", N, 12),
            F(6, "Amount, cardholder billing", N, 12),
            F(7, "Transmission date and time (MMDDhhmmss)", N, 10),
            F(8, "Amount, cardholder billing fee", N, 8),
            F(9, "Conversion rate, settlement", N, 8),
            F(10, "Conversion rate, cardholder billing", N, 8),
            F(11, "System trace audit number (STAN)", N, 6),
            F(12, "Time, local transaction (hhmmss)", N, 6),
            F(13, "Date, local transaction (MMDD)", N, 4),
            F(14, "Date, expiration (YYMM)", N, 4, s: Sensitivity.Secret),
            F(15, "Date, settlement (MMDD)", N, 4),
            F(16, "Date, conversion (MMDD)", N, 4),
            F(17, "Date, capture (MMDD)", N, 4),
            F(18, "Merchant type (MCC)", N, 4),
            F(19, "Acquiring institution country code", N, 3),
            F(20, "PAN extended, country code", N, 3),
            F(21, "Forwarding institution country code", N, 3),
            F(22, "Point of service entry mode", N, 3),
            F(23, "Card sequence number", N, 3),
            F(24, "Network international identifier (NII)", N, 3),
            F(25, "Point of service condition code", N, 2),
            F(26, "Point of service PIN capture code", N, 2),
            F(27, "Authorizing identification response length", N, 1),
            F(28, "Amount, transaction fee", XN, 9),
            F(29, "Amount, settlement fee", XN, 9),
            F(30, "Amount, transaction processing fee", XN, 9),
            F(31, "Amount, settlement processing fee", XN, 9),
            F(32, "Acquiring institution identification code", N, 11, LL),
            F(33, "Forwarding institution identification code", N, 11, LL),
            F(34, "Primary account number, extended", ANS, 28, LL, Sensitivity.Pan),
            F(35, "Track 2 data", Z, 37, LL, Sensitivity.Secret),
            F(36, "Track 3 data", N, 104, LLL, Sensitivity.Secret),
            F(37, "Retrieval reference number", AN, 12),
            F(38, "Authorization identification response", AN, 6),
            F(39, "Response code", AN, 2),
            F(40, "Service restriction code", AN, 3),
            F(41, "Card acceptor terminal identification", ANS, 8),
            F(42, "Card acceptor identification code", ANS, 15),
            F(43, "Card acceptor name and location", ANS, 40),
            F(44, "Additional response data", AN, 25, LL),
            F(45, "Track 1 data", AN, 76, LL, Sensitivity.Secret),
            F(48, "Additional data, private", ANS, 999, LLL),
            F(49, "Currency code, transaction", N, 3),
            F(50, "Currency code, settlement", N, 3),
            F(51, "Currency code, cardholder billing", N, 3),
            F(52, "Personal identification number (PIN) data", B, 8, s: Sensitivity.Secret),
            F(53, "Security related control information", N, 16, s: Sensitivity.Secret),
            F(54, "Additional amounts", AN, 120, LLL),
            F(55, "ICC data (EMV)", B, 255, LLL),
            F(64, "Message authentication code (MAC)", B, 8, s: Sensitivity.Secret),
            F(66, "Settlement code", N, 1),
            F(70, "Network management information code", N, 3),
            F(90, "Original data elements", N, 42),
            F(95, "Replacement amounts", AN, 42),
            F(100, "Receiving institution identification code", N, 11, LL),
            F(102, "Account identification 1", ANS, 28, LL, Sensitivity.Pan),
            F(103, "Account identification 2", ANS, 28, LL, Sensitivity.Pan),
            F(128, "Message authentication code (MAC)", B, 8, s: Sensitivity.Secret),
        };
        foreach (var n in new[] { 46, 47 }.Concat(Enumerable.Range(56, 8)).Concat(Enumerable.Range(111, 17)))
            list.Add(F(n, n <= 47 ? "Additional data (ISO)" : n <= 59 ? "Reserved for ISO use" : n <= 63 ? "Reserved for private use" : n <= 119 ? "Reserved for national use" : "Reserved for private use", ANS, 999, LLL));
        return list;
    }
}
