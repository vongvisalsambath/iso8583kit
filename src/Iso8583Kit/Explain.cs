namespace Iso8583Kit;

/// <summary>
/// What codes mean, in plain English. The MTI parts are defined by ISO 8583. Processing codes, entry modes and response
/// codes are given with their common meanings; card networks and hosts define variations and codes of their own.
/// </summary>
public static class Explain
{
    /// <summary>The MTI's second digit: what kind of message it is.</summary>
    public static string? MtiClass(char c) => c switch
    {
        '1' => "authorization", '2' => "financial (moves money)", '3' => "file action", '4' => "reversal or chargeback",
        '5' => "reconciliation", '6' => "administrative", '7' => "fee collection", '8' => "network management", _ => null,
    };

    /// <summary>The MTI's third digit: what it does.</summary>
    public static string? MtiFunction(char c) => c switch
    {
        '0' => "request", '1' => "response to a request", '2' => "advice (already done; informing)", '3' => "response to an advice",
        '4' => "notification", '5' => "notification acknowledgement", '6' => "instruction", '7' => "instruction acknowledgement", _ => null,
    };

    /// <summary>The MTI's fourth digit: who sent it.</summary>
    public static string? MtiOrigin(char c) => c switch
    {
        '0' => "the acquirer", '1' => "the acquirer, repeated", '2' => "the issuer", '3' => "the issuer, repeated", '4' => "another party", '5' => "another party, repeated", _ => null,
    };

    /// <summary>A whole MTI in a sentence, such as "0200: financial (moves money) request from the acquirer (1987 version)".</summary>
    public static string Mti(string mti)
    {
        if (mti is not { Length: 4 }) return "not a message type indicator";
        var version = mti[0] switch { '0' => "1987 version", '1' => "1993 version", '2' => "2003 version", _ => "unknown version" };
        return $"{MtiClass(mti[1]) ?? "unknown class"} {MtiFunction(mti[2]) ?? "unknown function"} from {MtiOrigin(mti[3]) ?? "an unknown origin"} ({version})";
    }

    /// <summary>Field 3: the transaction type and the accounts it uses.</summary>
    public static string? ProcessingCode(string code)
    {
        if (code is not { Length: 6 }) return null;
        var type = code[..2] switch
        {
            "00" => "purchase", "01" => "cash withdrawal", "02" => "debit adjustment", "09" => "purchase with cash back", "11" => "quasi-cash",
            "17" => "cash advance", "20" => "refund", "21" => "deposit", "22" => "credit adjustment", "26" => "original credit",
            "28" => "payment", "30" => "balance inquiry", "31" => "balance inquiry", "38" => "mini statement", "40" => "transfer",
            "50" => "bill payment", "90" => "PIN change", "91" => "PIN unblock", _ => null,
        };
        if (type is null) return null;
        string Account(string a) => a switch { "00" => "default account", "10" => "savings", "20" => "checking", "30" => "credit", "40" => "universal", _ => $"account type {a}" };
        return code[2..] == "0000" ? type : $"{type}, from {Account(code[2..4])}" + (code[4..] == "00" ? "" : $" to {Account(code[4..])}");
    }

    /// <summary>Field 22's first two digits: how the card number was read.</summary>
    public static string? EntryMode(string two) => two switch
    {
        "00" => "unknown", "01" => "typed in by hand", "02" => "magnetic stripe", "03" => "barcode", "04" => "optical character reader",
        "05" => "chip", "07" => "contactless chip", "10" => "stored credential (card on file)", "79" => "chip unreadable, typed in",
        "80" => "chip unreadable, fell back to the magnetic stripe", "81" => "e-commerce", "90" => "magnetic stripe, full track",
        "91" => "contactless magnetic stripe", "95" => "chip, without full data integrity", _ => null,
    };

    /// <summary>Field 22's third digit: PIN capability.</summary>
    public static string? PinCapability(char c) => c switch { '0' => "unknown", '1' => "the terminal can accept a PIN", '2' => "the terminal can't accept a PIN", '8' => "the PIN pad is down", _ => null };

    /// <summary>Field 39, with its commonly used meaning.</summary>
    public static string? ResponseCode(string code) => code switch
    {
        "00" => "approved", "01" => "refer to the card issuer", "02" => "refer to the card issuer (special condition)", "03" => "invalid merchant",
        "04" => "pick up the card", "05" => "do not honour", "06" => "error", "07" => "pick up the card (special condition)", "08" => "honour with identification",
        "10" => "approved for a partial amount", "11" => "approved (VIP)", "12" => "invalid transaction", "13" => "invalid amount", "14" => "invalid card number",
        "15" => "no such issuer", "19" => "re-enter the transaction", "21" => "no action taken", "25" => "record not found", "30" => "format error",
        "39" => "no credit account", "41" => "lost card: pick up", "43" => "stolen card: pick up", "51" => "insufficient funds", "52" => "no checking account",
        "53" => "no savings account", "54" => "expired card", "55" => "incorrect PIN", "56" => "no card record", "57" => "transaction not permitted to the cardholder",
        "58" => "transaction not permitted to the terminal", "59" => "suspected fraud", "61" => "exceeds the withdrawal amount limit", "62" => "restricted card",
        "63" => "security violation", "65" => "exceeds the withdrawal frequency limit", "68" => "response received too late", "75" => "too many PIN attempts",
        "76" => "invalid account", "77" => "inconsistent with the original", "78" => "card not activated", "85" => "no reason to decline (verification)",
        "91" => "issuer or switch unavailable", "92" => "can't route the transaction", "94" => "duplicate transmission", "96" => "system malfunction", _ => null,
    };

    /// <summary>Field 70: network management codes.</summary>
    public static string? NetworkCode(string code) => code switch
    {
        "001" => "sign on", "002" => "sign off", "101" => "key change", "161" => "key change", "201" => "cutover", "301" => "echo test (are you there?)", _ => null,
    };

    /// <summary>Field 25: point of service condition.</summary>
    public static string? ConditionCode(string code) => code switch
    {
        "00" => "normal", "01" => "customer not present", "02" => "unattended terminal", "03" => "merchant suspicious", "05" => "customer present, card not present",
        "08" => "mail or telephone order", "51" => "account verification", "59" => "e-commerce", _ => null,
    };

    private static readonly Dictionary<string, (string Code, int Decimals)> Currencies = new()
    {
        ["036"] = ("AUD", 2), ["116"] = ("KHR", 2), ["124"] = ("CAD", 2), ["156"] = ("CNY", 2), ["344"] = ("HKD", 2), ["356"] = ("INR", 2),
        ["360"] = ("IDR", 2), ["392"] = ("JPY", 0), ["410"] = ("KRW", 0), ["418"] = ("LAK", 2), ["458"] = ("MYR", 2), ["608"] = ("PHP", 2),
        ["702"] = ("SGD", 2), ["704"] = ("VND", 0), ["764"] = ("THB", 2), ["784"] = ("AED", 2), ["826"] = ("GBP", 2), ["840"] = ("USD", 2),
        ["901"] = ("TWD", 2), ["978"] = ("EUR", 2), ["756"] = ("CHF", 2), ["048"] = ("BHD", 3), ["414"] = ("KWD", 3), ["554"] = ("NZD", 2),
        ["104"] = ("MMK", 2), ["050"] = ("BDT", 2), ["682"] = ("SAR", 2), ["710"] = ("ZAR", 2), ["986"] = ("BRL", 2), ["484"] = ("MXN", 2),
    };

    /// <summary>An ISO 4217 numeric currency code as its letters and decimals, such as 840 → USD, 2.</summary>
    public static (string Code, int Decimals)? Currency(string numeric) => Currencies.TryGetValue(numeric, out var c) ? c : null;

    /// <summary>An amount field (12 digits in minor units) in its currency, such as 000000002500 and 840 → 25.00 USD.</summary>
    public static string Amount(string digits, string? currency)
    {
        if (!digits.All(char.IsAsciiDigit) || digits.Length == 0) return digits;
        var c = currency is null ? null : Currency(currency);
        var decimals = c?.Decimals ?? 2;
        var value = decimal.Parse(digits, System.Globalization.CultureInfo.InvariantCulture) / (decimal)Math.Pow(10, decimals);
        return value.ToString("N" + decimals, System.Globalization.CultureInfo.InvariantCulture) + (c is null ? "" : " " + c.Value.Code);
    }

    /// <summary>A plain-English line about a field's value, or null when there's nothing to add to the value itself.</summary>
    public static string? Field(int number, string value, Iso8583Message message) => number switch
    {
        2 => value.All(char.IsAsciiDigit) ? $"{value.Length} digits, {(Rules.LuhnOk(value) ? "check digit correct" : "check digit WRONG")}, network {Brand(value)}" : null,
        3 => ProcessingCode(value),
        4 or 5 or 6 => Amount(value, message[number switch { 4 => 49, 5 => 50, _ => 51 }]),
        7 when value.Length == 10 => $"{value[2..4]}/{value[..2]} at {value[4..6]}:{value[6..8]}:{value[8..]} (UTC by convention)",
        12 when value.Length == 6 => $"{value[..2]}:{value[2..4]}:{value[4..]}",
        13 or 15 or 16 or 17 when value.Length == 4 => $"day {value[2..]} of month {value[..2]}",
        22 when value.Length == 3 => $"{EntryMode(value[..2]) ?? "entry mode " + value[..2]}; {PinCapability(value[2]) ?? "PIN capability " + value[2]}",
        25 => ConditionCode(value),
        39 => ResponseCode(value),
        49 or 50 or 51 => Currency(value)?.Code,
        55 => Emv.Summary(value),
        70 => NetworkCode(value),
        _ => null,
    };

    /// <summary>The card network suggested by a card number's first digits (the issuer identification number).</summary>
    public static string Brand(string pan)
    {
        if (pan.Length < 6 || !pan.All(char.IsAsciiDigit)) return "unknown";
        var two = int.Parse(pan[..2], System.Globalization.CultureInfo.InvariantCulture);
        var four = int.Parse(pan[..4], System.Globalization.CultureInfo.InvariantCulture);
        var six = int.Parse(pan[..6], System.Globalization.CultureInfo.InvariantCulture);
        if (pan[0] == '4') return "Visa";
        if (two is >= 51 and <= 55 || six is >= 222100 and <= 272099) return "Mastercard";
        if (two is 34 or 37) return "American Express";
        if (four is >= 3528 and <= 3589) return "JCB";
        if (two == 62) return "UnionPay";
        if (four == 6011 || two == 65) return "Discover";
        return "unknown";
    }
}
