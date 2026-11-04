using System.Globalization;
using System.Security.Cryptography;

namespace Iso8583Kit;

/// <summary>How the simulated host decides. For testing terminals and switches only: it approves by rules, not by checking any account.</summary>
public sealed record HostOptions
{
    /// <summary>Approve amounts up to this many minor units (cents); above it, decline with 61 (exceeds the limit). Default 100000 (1,000.00).</summary>
    public long ApproveUpTo { get; init; } = 100_000;

    /// <summary>
    /// When true (the default), an amount whose last two digits are a decline code answers with that code, so a tester
    /// can ask for any outcome: 10.51 declines with 51 (insufficient funds), 10.05 with 05 (do not honour). Amounts ending
    /// in 00 follow the other rules.
    /// </summary>
    public bool ResponseFromCents { get; init; } = true;

    /// <summary>The time used for expiry checks. Defaults to now.</summary>
    public DateTime? Now { get; init; }
}

/// <summary>A simulated card host: answers requests with responses, the way an issuer or switch would.</summary>
public static class Simulator
{
    private static readonly int[] Echo = [2, 3, 4, 7, 11, 12, 13, 22, 25, 32, 37, 41, 42, 49];

    /// <summary>
    /// The response to <paramref name="request"/>, and why, or null when no response is due (the request is itself a
    /// response, an acknowledgement or a notification).
    /// </summary>
    public static (Iso8583Message Response, string Reason)? Respond(Iso8583Message request, HostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= new HostOptions();
        var mti = request.Mti;
        if (mti is not { Length: 4 } || !mti.All(char.IsAsciiDigit) || mti[2] is not ('0' or '2')) return null; // only requests and advices are answered

        var response = new Iso8583Message(mti[..2] + (mti[2] == '0' ? '1' : '3') + mti[3]);
        foreach (var n in Echo) if (request[n] is { } v) response[n] = v;

        string Answer(string code, string reason)
        {
            response[39] = code;
            return reason;
        }

        var issues = Rules.Check(request, now: options.Now ?? DateTime.UtcNow);
        string why;
        switch (mti[1])
        {
            case '8':
                if (request[70] is { } nm) response[70] = nm;
                why = Answer("00", $"network management: {Explain.NetworkCode(request[70] ?? "") ?? "acknowledged"}");
                break;
            case '4':
                if (request[90] is { } original) response[90] = original;
                why = Answer("00", "reversal accepted");
                break;
            case '1' or '2' when mti[2] == '0':
            {
                if (issues.FirstOrDefault(i => i.IsError && i.Code is "FORMAT" or "MTI" or "UNDEFINED" or "MISSING" or "DATE") is { } bad)
                { why = Answer("30", "format error: " + bad.Message); break; }
                if (issues.Any(i => i.Code is "PAN-LUHN" or "PAN")) { why = Answer("14", "the card number is invalid"); break; }
                if (issues.Any(i => i.Code == "EXPIRED")) { why = Answer("54", "the card has expired"); break; }
                var amount = request[4] is { Length: 12 } a && a.All(char.IsAsciiDigit) ? long.Parse(a, CultureInfo.InvariantCulture) : 0;
                var cents = (amount % 100).ToString("00", CultureInfo.InvariantCulture);
                if (options.ResponseFromCents && cents != "00" && Explain.ResponseCode(cents) is string meaning && cents is not ("10" or "11" or "85"))
                { why = Answer(cents, $"test amount ending in {cents}: {meaning}"); break; }
                if (amount > options.ApproveUpTo) { why = Answer("61", $"the amount is over the host's limit of {options.ApproveUpTo} minor units"); break; }
                response[38] = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000", CultureInfo.InvariantCulture);
                why = Answer("00", "approved");
                break;
            }
            case '1' or '2':
                why = Answer("00", "advice acknowledged");
                break;
            default:
                why = Answer("12", $"the simulator doesn't handle {Explain.Mti(mti)} messages");
                break;
        }
        return (response, why);
    }
}
