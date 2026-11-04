using Xunit;
using static Iso8583Kit.Tests.CodecTests;

namespace Iso8583Kit.Tests;

public sealed class RulesTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 9, 30, 0, DateTimeKind.Utc);
    private static IEnumerable<string> Codes(Iso8583Message m) => Rules.Check(m, now: Now).Select(i => i.Code);

    [Fact]
    public void AGoodPurchaseHasNoProblems() => Assert.Empty(Rules.Check(Purchase(), now: Now).Select(i => i.Code + ": " + i.Message));

    [Fact]
    public void CardNumbersAreChecked()
    {
        Assert.True(Rules.LuhnOk(Visa));
        Assert.True(Rules.LuhnOk(Mastercard));
        Assert.False(Rules.LuhnOk("4111111111111112"));
        var m = Purchase();
        m[2] = "4111111111111112";
        Assert.Contains("PAN-LUHN", Codes(m));
        m[2] = "41111";
        Assert.Contains("PAN", Codes(m));
    }

    [Fact]
    public void DatesAndTimesAreChecked()
    {
        var m = Purchase();
        m[7] = "1332093015";
        m[12] = "256000";
        m[13] = "0230";
        Assert.Equal(3, Codes(m).Count(c => c == "DATE"));
        m = Purchase();
        m[14] = "2509";
        Assert.Contains("EXPIRED", Codes(m));
        m[14] = "2813";
        Assert.Contains("EXPIRY", Codes(m));
    }

    [Fact]
    public void RequiredFieldsDependOnTheMessage()
    {
        var response = Purchase().Clone("0210");
        Assert.Contains("MISSING", Codes(response));
        var noCard = Purchase();
        noCard[2] = null;
        noCard[35] = null;
        Assert.Contains("MISSING", Codes(noCard));
        Assert.Contains("MTI", Codes(new Iso8583Message("0900")));
        Assert.Contains("CURRENCY", Codes(new Iso8583Message("0200") { [2] = Visa, [49] = "999" }));
    }

    [Fact]
    public void SensitiveValuesAreMasked()
    {
        Assert.Equal("411111******1111", Rules.MaskPan(Visa));
        Assert.Equal("***4321", Rules.MaskPan("1234321"));
        var spec = Iso8583Spec.Default1987;
        Assert.Equal("411111******1111", Rules.Display(spec[2], Visa));
        Assert.Equal("[hidden: 37 characters]", Rules.Display(spec[35], Purchase()[35]!));
        Assert.Equal("[hidden: 16 hex digits]", Rules.Display(spec[52], "0123456789ABCDEF"));
        Assert.Equal("[hidden: 4 characters]", Rules.Display(spec[14], "2812"));
        Assert.Equal("TERM????", Rules.Display(spec[41], "TERM\u001B\u202E\n\t"));
        var m = Purchase();
        foreach (var (n, v) in m.Fields)
            Assert.DoesNotContain(Visa, Explain.Field(n, v, m) ?? "");
    }

    [Fact]
    public void CodesAreExplained()
    {
        Assert.Equal("financial (moves money) request from the acquirer (1987 version)", Explain.Mti("0200"));
        Assert.Equal("network management response to a request from the acquirer (1987 version)", Explain.Mti("0810"));
        Assert.Equal("cash withdrawal, from savings", Explain.ProcessingCode("011000"));
        Assert.Equal("purchase", Explain.ProcessingCode("000000"));
        Assert.Equal("insufficient funds", Explain.ResponseCode("51"));
        Assert.Equal("25.00 USD", Explain.Amount("000000002500", "840"));
        Assert.Equal("2,500 JPY", Explain.Amount("000000002500", "392"));
        Assert.Equal("chip; the terminal can accept a PIN", Explain.Field(22, "051", Purchase()));
        Assert.Equal(("Visa", "Mastercard", "American Express"), (Explain.Brand(Visa), Explain.Brand(Mastercard), Explain.Brand("378282246310005")));
    }

    [Fact]
    public void EmvChipDataIsRead()
    {
        var (tags, issues) = Emv.Parse(Purchase()[55]!);
        Assert.Equal([], issues);
        Assert.Equal(["9F26", "9F27", "9F10", "95", "9A", "9C", "9F02", "5F2A", "82"], tags.Select(t => t.Tag));
        Assert.Equal("Application cryptogram", tags[0].Name);
        Assert.Contains("ARQC", Emv.Summary(Purchase()[55]!));

        // card data inside the chip data is hidden like the fields it mirrors
        var withPan = Emv.Parse("5A08411111111111111157114111111111111111D28121010000012300000F");
        Assert.All(withPan.Tags, t => Assert.StartsWith("[hidden", t.Value));
        Assert.Contains(Emv.Parse("9F2608A1B2").Issues, i => i.Contains("only", StringComparison.Ordinal));
        Assert.Contains(Emv.Parse("9F").Issues, i => i.Contains("no length", StringComparison.Ordinal) || i.Contains("ends", StringComparison.Ordinal));
        Assert.Contains(Emv.Parse("9F2685").Issues, i => i.Contains("invalid length", StringComparison.Ordinal));
        // a nesting bomb stops at 4 levels; a flood stops at 64 objects
        Assert.Contains(Emv.Parse("700C700A700870067004700270" + "00").Issues, i => i.Contains("4 levels", StringComparison.Ordinal));
        Assert.Contains(Emv.Parse(string.Concat(Enumerable.Repeat("9C0100", 100))).Issues, i => i.Contains("64", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSimulatorAnswersLikeAHost()
    {
        var o = new HostOptions { Now = Now };
        var (approved, why) = Simulator.Respond(Purchase(), o)!.Value;
        Assert.Equal(("0210", "00", "approved"), (approved.Mti, approved[39], why));
        Assert.Matches("^[0-9]{6}$", approved[38]!);
        Assert.Equal(Purchase()[11], approved[11]);
        Assert.Null(approved[35]); // track data and PIN blocks aren't echoed
        Assert.Null(approved[52]);

        string Code(Action<Iso8583Message> change, HostOptions? options = null)
        {
            var m = Purchase();
            change(m);
            return Simulator.Respond(m, options ?? o)!.Value.Response[39]!;
        }
        Assert.Equal("51", Code(m => m[4] = "000000001051"));
        Assert.Equal("05", Code(m => m[4] = "000000001005"));
        Assert.Equal("61", Code(m => m[4] = "000000500000"));
        Assert.Equal("00", Code(m => m[4] = "000000001051", o with { ResponseFromCents = false }));
        Assert.Equal("14", Code(m => m[2] = "4111111111111112"));
        Assert.Equal("54", Code(m => m[14] = "2509"));
        Assert.Equal("30", Code(m => m[7] = "9999999999"));

        Assert.Equal(("0810", "00", "301"), Simulator.Respond(new Iso8583Message("0800") { [70] = "301", [11] = "000001" })!.Value.Response is var e ? (e.Mti, e[39], e[70]) : default);
        Assert.Equal("0410", Simulator.Respond(new Iso8583Message("0400") { [2] = Visa, [11] = "000002" })!.Value.Response.Mti);
        Assert.Equal(("0130", "00"), Simulator.Respond(new Iso8583Message("0120") { [2] = Visa })!.Value.Response is var a ? (a.Mti, a[39]) : default);
        Assert.Null(Simulator.Respond(new Iso8583Message("0210")));
        Assert.Null(Simulator.Respond(new Iso8583Message("02X0")));
        Assert.Equal("12", Simulator.Respond(new Iso8583Message("0600"))!.Value.Response[39]);
    }
}
