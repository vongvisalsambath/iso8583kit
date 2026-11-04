# Iso8583Kit

Read, write, check and explain **ISO 8583** messages: the format behind most card payments and ATM withdrawals. It comes in three parts:
- a .NET library;
- an `iso8583` command with a simulated card host for testing terminals and switches;
- a [website](site/Iso8583Kit.Web) that explains a pasted message entirely in the browser.

Card numbers are masked everywhere, and track data, PIN blocks and MACs are never shown.

```
$ iso8583 parse samples/purchase-ascii.txt
MTI 0200: financial (moves money) request from the acquirer (1987 version)
    2 Primary account number (PAN)                  411111******1111  = 16 digits, check digit correct, network Visa
    3 Processing code                               000000  = purchase
    4 Amount, transaction                           000000002500  = 25.00 USD
   14 Date, expiration (YYMM)                       [hidden: 4 characters]
   22 Point of service entry mode                   051  = chip; the terminal can accept a PIN
   55 ICC data (EMV)                                9F2608A1B2…  = 4 EMV data objects; cryptogram type ARQC (asks the issuer to decide)
        9F26  Application cryptogram                   A1B2C3D4E5F60708
        9F27  Cryptogram information data              80
   …
```

## Library

```csharp
using Iso8583Kit;

var m = new Iso8583Message("0200")
{
    [2] = "4111111111111111", [3] = "000000", [4] = "000000002500", [11] = "000123", [49] = "840",
};
byte[] bytes = Iso8583Codec.Pack(m, encoding: Iso8583Encoding.BcdBinary);

var read = Iso8583Codec.Unpack(bytes, encoding: Iso8583Encoding.BcdBinary);   // never throws for bad input
foreach (var (n, value) in read.Message!.Fields)
    Console.WriteLine($"{n} {Iso8583Spec.Default1987[n]!.Name}: {Rules.Display(Iso8583Spec.Default1987[n], value)}  {Explain.Field(n, value, read.Message)}");

var issues = Rules.Check(read.Message);   // Luhn, dates, expiry, required fields, codes
```

- **Fields.** `Iso8583Spec.Default1987` defines the 1987 data elements as commonly used: their format (n, an, ans, b, z, x+n) and fixed, LLVAR or LLLVAR length. Fields reserved for national and private use are generic LLLVAR text; you can build your own spec for a network's variant.
- **Layouts.** Networks lay the same message out differently. Three common layouts are supported:
  - `Ascii`: everything as text, the bitmap as 16 hexadecimal characters, binary fields as hexadecimal text;
  - `AsciiBinaryBitmap`;
  - `BcdBinary`: packed BCD numbers and lengths, raw binary bitmap and binary fields.
- **Unpacking** never throws. It refuses lengths beyond a field's maximum, undefined fields, bit 65 and data after the last field. It reports where each field starts (`Spans`), so a viewer can highlight bytes.
- **Checks.** These include:
  - the card number's Luhn check digit;
  - real dates and times, and expired cards;
  - the MTI's class and function;
  - the fields a request or response needs;
  - known currencies, entry modes and response codes;
  - the EMV data in field 55.
- **Explanations** cover the MTI, processing code, amounts in their currency, entry mode, condition code, response code, network management code, the card network suggested by the card number, and every EMV tag in field 55, including the cryptogram type (ARQC, TC, AAC).

The MTI parts are ISO 8583's own. Processing codes, entry modes and response codes have common meanings that most networks share, but each network defines its own variations, and the explanations say what is usual, not what a particular network specifies.

## Simulated host

```
iso8583 host --port 8583                     # answers on 127.0.0.1 only
iso8583 send 127.0.0.1:8583 --mti 0200 --field 2=4111111111111111 --field 4=000000001051 --field 11=000124 --field 49=840
```

The host answers:
- `0100`/`0200` with `0110`/`0210`;
- `0800` with `0810`;
- reversals with `0410`;
- advices with an acknowledgement.

Messages are framed with a 2-byte length. It approves by rules, never by checking an account:
- **Amounts ending in a decline code** decline with that code: 10.51 gives 51 (insufficient funds), 10.05 gives 05 (do not honour). `--no-test-amounts` turns this off.
- **A wrong check digit** gives 14, an **expired card** 54, a **malformed message** 30, and an **amount over `--limit`** 61.
- Track data and PIN blocks aren't echoed in responses, and its log shows card numbers masked.

## Website

`site/Iso8583Kit.Web` is a Blazor WebAssembly page built on the same library. You paste a message in hexadecimal or as text, and it does the following:
- tries the three layouts and picks the one that reads;
- explains every field and decodes the EMV chip data;
- shows what the simulated host would answer.

```
dotnet publish site/Iso8583Kit.Web -c Release -o out
cd site/test && npm ci && npx playwright install chromium && node e2e.cjs ../../out/wwwroot
```

## Tests

`dotnet test` runs:
- round trips in every layout, and the 0800 echo test against its well-known bytes;
- broken messages of every kind;
- the checks, masking and explanations;
- the EMV reader, including nesting and flooding limits;
- the simulator's decisions;
- the TCP host over real connections: oversized frames, garbage and connection floods;
- 60,000 randomly damaged messages, which must never throw, never take more than 200 ms, and never put the test card number into a message.

The card numbers in examples and tests are the card networks' public test numbers.

## Security

See [SECURITY.md](SECURITY.md).

## Licence

Apache-2.0 (see [LICENSE](LICENSE)). Not affiliated with ISO or any card network. Written with the help of Claude (Anthropic).
