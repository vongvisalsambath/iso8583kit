using System.Text;

namespace Iso8583Kit.Web.Pages;

public partial class Home
{
    private static readonly Iso8583Spec Spec = Iso8583Spec.Default1987;
    private const int MaxInput = 40_000;

    private static readonly (string Label, string Text)[] Examples = BuildExamples();

    private string input = "";
    private string layout = "auto";
    private UnpackResult? result;
    private Iso8583Encoding encoding = Iso8583Encoding.Ascii;
    private List<Iso8583Issue> issues = [];
    private Iso8583Message? reply;
    private string replyReason = "";

    protected override void OnInitialized() => Use(Examples[0].Text);

    private void Use(string text)
    {
        input = text;
        layout = "auto";
        Read();
    }

    private void Read()
    {
        result = null;
        reply = null;
        issues = [];
        var text = input ?? "";
        if (text.Trim().Length == 0) return;
        if (text.Length > MaxInput)
        {
            result = new UnpackResult(null, [new Iso8583Issue("TOO-LONG", "That is longer than any ISO 8583 message.")], []);
            issues = [.. result.Issues];
            return;
        }
        var compact = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var isHex = compact.Length % 2 == 0 && compact.All(char.IsAsciiHexDigit);
        var bytes = isHex ? Convert.FromHexString(compact) : Encoding.Latin1.GetBytes(text.Trim());
        var candidates = layout switch
        {
            "ascii" => [Iso8583Encoding.Ascii],
            "ascii-binary" => [Iso8583Encoding.AsciiBinaryBitmap],
            "bcd" => [Iso8583Encoding.BcdBinary],
            _ => new[] { Iso8583Encoding.Ascii, Iso8583Encoding.BcdBinary, Iso8583Encoding.AsciiBinaryBitmap },
        };
        // a hex string can also be an ASCII message made only of hex characters; try both readings
        var readings = candidates.Select(e => (e, r: Iso8583Codec.Unpack(bytes, encoding: e)))
            .Concat(isHex && layout is "auto" or "ascii" ? [(Iso8583Encoding.Ascii, Iso8583Codec.Unpack(Encoding.ASCII.GetBytes(compact)))] : [])
            .ToList();
        var best = readings.OrderByDescending(x => x.r.IsValid).ThenByDescending(x => x.r.Message is not null).ThenByDescending(x => x.r.Spans.Count).First();
        (encoding, result) = best;
        issues = [.. result.Issues];
        if (result.Message is { } m)
        {
            issues.AddRange(Rules.Check(m, now: DateTime.UtcNow).Where(i => !issues.Any(x => x.Code == i.Code && x.Field == i.Field)));
            if (Simulator.Respond(m) is { } answer) (reply, replyReason) = answer;
        }
    }

    private static (string, string)[] BuildExamples()
    {
        const string visa = "4111111111111111";
        var purchase = new Iso8583Message("0200")
        {
            [2] = visa, [3] = "000000", [4] = "000000002500", [7] = "1104093015", [11] = "000123", [12] = "163015", [13] = "1104", [14] = "2812",
            [22] = "051", [25] = "00", [35] = visa + "=28121010000012300000", [37] = "430809123456", [41] = "TERM0001", [42] = "DEMO MERCHANT01",
            [43] = "DEMO COFFEE".PadRight(23) + "PHNOM PENH".PadRight(13) + "12KH", [49] = "840",
            [55] = "9F2608A1B2C3D4E5F607089F2701809F10120110A00003220000000000000000000000FF950500000000009A032611049C01009F02060000000025005F2A02084082021900",
        };
        var declined = purchase.Clone("0210");
        declined[35] = null;
        declined[55] = null;
        declined[4] = "000000001051";
        declined[39] = "51";
        var withdrawal = new Iso8583Message("0200") { [2] = "5555555555554444", [3] = "011000", [4] = "000000010000", [7] = "1104093015", [11] = "000124", [14] = "2812", [22] = "021", [41] = "ATM00001", [49] = "116", [52] = "1A2B3C4D5E6F7081" };
        var echo = new Iso8583Message("0800") { [7] = "1104093015", [11] = "000001", [70] = "301" };
        var typo = purchase.Clone();
        typo[2] = "4111111111111112";
        typo[35] = null;
        string Ascii(Iso8583Message m) => Encoding.ASCII.GetString(Iso8583Codec.Pack(m));
        string Hex(Iso8583Message m, Iso8583Encoding e) => string.Join(" ", Convert.ToHexString(Iso8583Codec.Pack(m, encoding: e)).Chunk(16).Select(c => new string(c)));
        return
        [
            ("Chip purchase (ASCII)", Ascii(purchase)),
            ("Chip purchase (BCD, hex)", Hex(purchase, Iso8583Encoding.BcdBinary)),
            ("Declined: insufficient funds", Ascii(declined)),
            ("ATM withdrawal in riel", Hex(withdrawal, Iso8583Encoding.BcdBinary)),
            ("Echo test", Ascii(echo)),
            ("A mistyped card number", Ascii(typo)),
            ("A cut-off message", Ascii(purchase)[..60]),
        ];
    }
}
