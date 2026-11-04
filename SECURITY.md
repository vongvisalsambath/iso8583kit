# Security

ISO 8583 messages carry card numbers, track data and PIN blocks: data that must never leak into logs, screens or error messages. They also arrive over the network from systems that may be broken or hostile. Iso8583Kit is built for both.

## Reporting a problem

Please report vulnerabilities privately through GitHub's **Security → Report a vulnerability** on this repository, not in a public issue.

## What is protected, and how

| Threat | Protection | Tested by |
|---|---|---|
| **Card data in logs and on screen** | Every field has a sensitivity:<ul><li>Card numbers (fields 2, 34, 102, 103) are shown as the first 6 and last 4 digits only.</li><li>Track 1, 2 and 3, the expiry date, PIN data, security information and MACs are shown only as "[hidden: n characters]".</li><li>Card data inside EMV field 55 (tags 57, 5A, 5F20, 5F24) is hidden too.</li></ul>Error messages never quote a field's value. The command, its JSON, the simulator's log and the website all use the same rules. | `SensitiveValuesAreMasked`; 60,000 damaged messages never put the card number in a message; the host's log never contains it; the website never shows it |
| **Hostile messages** | Messages over 16 KB are refused. Lengths beyond a field's maximum, fields the spec doesn't define, bit 65 and trailing bytes are all errors. Text fields must be printable ASCII. `Unpack` never throws. | `BrokenMessagesAreReportedNotThrown`, `ControlBytesInTextFieldsAreRefused`, `TwentyThousandDamagedMessages…` (each layout, under 200 ms) |
| **EMV data** | BER-TLV lengths are checked against the data. Nesting stops at 4 levels and reading at 64 objects. | `EmvChipDataIsRead` (truncated, invalid length, nesting bomb, flood) |
| **The simulated host** | It listens on 127.0.0.1 unless `--allow-remote` is given, because it approves anything that looks right. Frames of 0 bytes or over 16 KB close the connection, and so does an unreadable message, since the stream can't be trusted after it. There is a 30-second idle timeout, and at most 64 connections; more are refused. Responses don't echo track data or PIN blocks. | `HostTests` (real TCP: oversized and empty frames, garbage, 74 connections at once); manual flood of 100 |
| **Terminal escapes** | Everything printed goes through printable-ASCII filtering, and JSON output escapes. | `ControlBytesInTextFieldsAreRefused`, website hostile-input checks |
| **The website** | Everything runs in the browser; nothing is uploaded. The Content-Security-Policy allows only the page's own files. Blazor writes every value as text. The page can't be framed. It asks people to use test card numbers only. | `site/test/e2e.cjs` |
| **Supply chain** | The library has no dependencies. The browser tests use Playwright at an exact version with a lockfile. CI runs `npm audit` and `dotnet list package --vulnerable`, and pins GitHub Actions to commits. | CI |

## What it doesn't do

- It doesn't encrypt or decrypt PIN blocks, compute or verify MACs, or handle keys. Those belong in a hardware security module.
- It isn't certified by any card network, and its simulator isn't an authorization system.
- Masking protects what Iso8583Kit shows. A program that logs `message[2]` itself still logs a card number.
