using System.Globalization;

namespace Iso8583Kit;

/// <summary>Checks on field contents, and masking. Messages from here never repeat a field's value: it may be a card number.</summary>
public static class Rules
{
    /// <summary>Why a value doesn't fit its field's format (without quoting it), or null if it does.</summary>
    public static string? CheckFormat(FieldDefinition def, string value)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(value);
        return def.Format switch
        {
            FieldFormat.N when !value.All(char.IsAsciiDigit) => "should be digits only.",
            FieldFormat.AN when !value.All(c => char.IsAsciiLetterOrDigit(c) || c == ' ') => "should be letters and digits only.",
            FieldFormat.ANS when !value.All(c => c is >= ' ' and <= '~') => "should be printable ASCII only.",
            FieldFormat.Z when !value.All(c => char.IsAsciiDigit(c) || c is '=' or 'D') => "should be digits and the separator '='.",
            FieldFormat.XN when value.Length < 2 || value[0] is not ('C' or 'D') || !value[1..].All(char.IsAsciiDigit) => "should be C or D followed by digits.",
            FieldFormat.B when value.Length % 2 != 0 || !value.All(char.IsAsciiHexDigit) => "should be hexadecimal.",
            _ => null,
        };
    }

    /// <summary>True when a card number's last digit is its Luhn (mod 10) check digit.</summary>
    public static bool LuhnOk(string digits)
    {
        if (digits.Length < 2 || !digits.All(char.IsAsciiDigit)) return false;
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }

    /// <summary>A card number as it may be shown: the first 6 and last 4 digits, the rest as *. Short numbers show only the last 4.</summary>
    public static string MaskPan(string pan)
    {
        if (pan.Length < 13) return new string('*', Math.Max(0, pan.Length - 4)) + pan[Math.Max(0, pan.Length - 4)..];
        return pan[..6] + new string('*', pan.Length - 10) + pan[^4..];
    }

    /// <summary>A field's value as it may be shown or logged, following its <see cref="Sensitivity"/>.</summary>
    public static string Display(FieldDefinition? def, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return def?.Sensitivity switch
        {
            Sensitivity.Pan => value.All(char.IsAsciiDigit) ? MaskPan(value) : new string('*', value.Length),
            Sensitivity.Secret => $"[hidden: {value.Length} {(def.Format == FieldFormat.B ? "hex digits" : "characters")}]",
            _ => new string(value.Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray()),
        };
    }

    /// <summary>Checks a message's contents: the MTI, the fields a message class needs, and what each field holds.</summary>
    public static IReadOnlyList<Iso8583Issue> Check(Iso8583Message message, Iso8583Spec? spec = null, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        spec ??= Iso8583Spec.Default1987;
        var issues = new List<Iso8583Issue>();
        void Error(string code, string text, int? field = null) => issues.Add(new(code, text, field));
        void Warn(string code, string text, int? field = null) => issues.Add(new(code, text, field, IsError: false));

        var mti = message.Mti ?? "";
        if (!Iso8583Message.MtiPattern().IsMatch(mti)) { Error("MTI", "The message type indicator must be four digits."); return issues; }
        if (mti[0] != '0') Warn("MTI-VERSION", $"MTI version {mti[0]} isn't 1987 (0); this spec describes the 1987 fields.");
        if (Explain.MtiClass(mti[1]) is null) Error("MTI", $"MTI class {mti[1]} isn't defined.");
        if (Explain.MtiFunction(mti[2]) is null) Error("MTI", $"MTI function {mti[2]} isn't defined.");

        foreach (var (n, value) in message.Fields)
        {
            var def = spec[n];
            if (def is null) { Error("UNDEFINED", $"{spec.Name} doesn't define field {n}.", n); continue; }
            if (CheckFormat(def, value) is string p) Error("FORMAT", $"Field {n} ({def.Name}): {p}", n);
        }

        var pan = message[2];
        if (pan is not null && pan.All(char.IsAsciiDigit))
        {
            if (pan.Length < 12) Error("PAN", "The card number (field 2) is shorter than 12 digits.", 2);
            else if (!LuhnOk(pan)) Error("PAN-LUHN", "The card number's check digit (Luhn) is wrong: a typing or reading mistake.", 2);
        }
        if (message[3] is { Length: 6 } pc && Explain.ProcessingCode(pc) is null) Warn("PROCESSING-CODE", $"Transaction type {pc[..2]} in the processing code (field 3) isn't a common one.", 3);
        if (message[4] is { Length: 12 } amt && amt.All(char.IsAsciiDigit) && long.Parse(amt, CultureInfo.InvariantCulture) == 0 && mti[1] is '1' or '2' && message[3]?.StartsWith("30", StringComparison.Ordinal) != true)
            Warn("AMOUNT-ZERO", "The transaction amount (field 4) is zero.", 4);
        if (message[7] is { Length: 10 } dt && (!ValidDate(dt[..4], now) || !ValidTime(dt[4..]))) Error("DATE", "The transmission date and time (field 7) isn't a real MMDDhhmmss.", 7);
        if (message[12] is { Length: 6 } time && !ValidTime(time)) Error("DATE", "The local time (field 12) isn't a real hhmmss.", 12);
        foreach (var f in new[] { 13, 15, 16, 17 })
            if (message[f] is { Length: 4 } d && !ValidDate(d, now)) Error("DATE", $"Field {f} isn't a real MMDD date.", f);
        if (message[14] is { Length: 4 } exp && exp.All(char.IsAsciiDigit))
        {
            var month = int.Parse(exp[2..], CultureInfo.InvariantCulture);
            if (month is < 1 or > 12) Error("EXPIRY", "The expiry date (field 14) isn't a real YYMM.", 14);
            else if (now is DateTime n0 && new DateTime(2000 + int.Parse(exp[..2], CultureInfo.InvariantCulture), month, 1).AddMonths(1) <= n0)
                Warn("EXPIRED", "The card's expiry date (field 14) has passed.", 14);
        }
        foreach (var f in new[] { 49, 50, 51 })
            if (message[f] is { Length: 3 } c && Explain.Currency(c) is null) Warn("CURRENCY", $"Currency code {c} in field {f} isn't one this toolkit knows.", f);
        if (message[22] is { Length: 3 } pem && Explain.EntryMode(pem[..2]) is null) Warn("ENTRY-MODE", $"PAN entry mode {pem[..2]} (field 22) isn't a common one.", 22);
        if (message[39] is { } rc && mti[2] is '1' or '3' && Explain.ResponseCode(rc) is null) Warn("RESPONSE-CODE", $"Response code {Iso8583Codec.Show(rc)} isn't one this toolkit knows; networks add their own.", 39);
        if (mti[2] is '1' or '3' && message[39] is null && mti[1] != '8') Error("MISSING", "A response needs a response code (field 39).", 39);
        if (mti[1] is '1' or '2' && mti[2] is '0' && message[2] is null && message[35] is null && message[45] is null)
            Error("MISSING", "An authorization or financial request needs the card: field 2, 35 or 45.");
        if (mti[1] is '1' or '2' or '4' && message[11] is null) Warn("MISSING", "There is no trace number (field 11); matching a response to its request will be hard.", 11);
        if (mti[1] == '8' && message[70] is null) Warn("MISSING", "A network management message usually has a code in field 70 (001 sign-on, 002 sign-off, 301 echo test).", 70);
        if (message[55] is { } icc)
            foreach (var e in Emv.Parse(icc).Issues) Warn("ICC", "Field 55: " + e, 55);
        return issues;
    }

    private static bool ValidTime(string hhmmss) =>
        hhmmss.Length == 6 && hhmmss.All(char.IsAsciiDigit) && int.Parse(hhmmss[..2], CultureInfo.InvariantCulture) < 24
        && int.Parse(hhmmss[2..4], CultureInfo.InvariantCulture) < 60 && int.Parse(hhmmss[4..], CultureInfo.InvariantCulture) < 60;

    private static bool ValidDate(string mmdd, DateTime? now)
    {
        if (mmdd.Length != 4 || !mmdd.All(char.IsAsciiDigit)) return false;
        var m = int.Parse(mmdd[..2], CultureInfo.InvariantCulture);
        var d = int.Parse(mmdd[2..], CultureInfo.InvariantCulture);
        // no year: 29 February is allowed
        return m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(2024, m);
    }
}
