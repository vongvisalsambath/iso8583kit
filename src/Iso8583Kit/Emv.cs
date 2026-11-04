using System.Text;

namespace Iso8583Kit;

/// <summary>One EMV data object from field 55: a BER-TLV tag, its name, and its value in hexadecimal.</summary>
public sealed record EmvTag(string Tag, string Name, string Value);

/// <summary>Reads the EMV chip data (BER-TLV) that field 55 carries.</summary>
public static class Emv
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["4F"] = "Application identifier (AID)", ["50"] = "Application label", ["57"] = "Track 2 equivalent data", ["5A"] = "Application PAN",
        ["5F20"] = "Cardholder name", ["5F24"] = "Application expiry date", ["5F25"] = "Application effective date", ["5F2A"] = "Transaction currency code",
        ["5F34"] = "PAN sequence number", ["82"] = "Application interchange profile (AIP)", ["84"] = "Dedicated file name", ["8A"] = "Authorization response code",
        ["91"] = "Issuer authentication data", ["95"] = "Terminal verification results (TVR)", ["9A"] = "Transaction date", ["9B"] = "Transaction status information",
        ["9C"] = "Transaction type", ["9F02"] = "Amount, authorized", ["9F03"] = "Amount, other", ["9F06"] = "Application identifier (terminal)",
        ["9F09"] = "Application version number", ["9F10"] = "Issuer application data", ["9F1A"] = "Terminal country code", ["9F1E"] = "Interface device serial number",
        ["9F26"] = "Application cryptogram", ["9F27"] = "Cryptogram information data", ["9F33"] = "Terminal capabilities", ["9F34"] = "Cardholder verification method results",
        ["9F35"] = "Terminal type", ["9F36"] = "Application transaction counter (ATC)", ["9F37"] = "Unpredictable number", ["9F41"] = "Transaction sequence counter",
        ["9F53"] = "Transaction category code", ["9F6E"] = "Form factor indicator", ["71"] = "Issuer script template 1", ["72"] = "Issuer script template 2",
    };

    // tags that hold card or cardholder data: hidden like the message fields they mirror
    private static readonly HashSet<string> Secret = new(StringComparer.Ordinal) { "57", "5A", "5F20", "5F24", "9F1F", "56", "9F6B" };

    /// <summary>The objects in <paramref name="hex"/>, and problems found. At most 64 objects and 4 levels of nesting are read.</summary>
    public static (IReadOnlyList<EmvTag> Tags, IReadOnlyList<string> Issues) Parse(string hex)
    {
        var tags = new List<EmvTag>();
        var issues = new List<string>();
        if (!Iso8583Codec.TryHex(hex ?? "", out var bytes)) { issues.Add("isn't hexadecimal."); return (tags, issues); }
        Read(bytes, 0, bytes.Length, 0, tags, issues);
        return (tags, issues);
    }

    private static void Read(byte[] b, int pos, int end, int depth, List<EmvTag> tags, List<string> issues)
    {
        while (pos < end)
        {
            if (tags.Count >= 64) { issues.Add("more than 64 data objects; the rest weren't read."); return; }
            if (b[pos] is 0x00 or 0xFF) { pos++; continue; } // padding between objects
            var tagStart = pos;
            var constructed = (b[pos] & 0x20) != 0;
            if ((b[pos++] & 0x1F) == 0x1F)
            {
                // a multi-byte tag: continues while bit 8 is set (at most 3 more bytes)
                var more = 0;
                while (pos < end && (b[pos] & 0x80) != 0 && more < 3) { pos++; more++; }
                pos++;
            }
            if (pos > end) { issues.Add($"the data ends inside a tag at byte {tagStart}."); return; }
            var tag = Convert.ToHexString(b, tagStart, pos - tagStart);
            if (pos >= end) { issues.Add($"tag {tag} has no length."); return; }
            int length = b[pos++];
            if (length > 0x7F)
            {
                var n = length & 0x7F;
                if (n is 0 or > 2 || pos + n > end) { issues.Add($"tag {tag} has an invalid length."); return; }
                length = 0;
                for (var i = 0; i < n; i++) length = (length << 8) | b[pos++];
            }
            if (pos + length > end) { issues.Add($"tag {tag} says it is {length} bytes long, but only {end - pos} are left."); return; }
            var value = Convert.ToHexString(b, pos, length);
            var name = Names.GetValueOrDefault(tag, "Unknown tag");
            tags.Add(new EmvTag(tag, name, Secret.Contains(tag) ? $"[hidden: {length} bytes]" : value));
            if (constructed)
            {
                if (depth >= 4) issues.Add($"tag {tag} nests more than 4 levels deep; its contents weren't read.");
                else Read(b, pos, pos + length, depth + 1, tags, issues);
            }
            pos += length;
        }
    }

    /// <summary>A short description: the number of objects and the cryptogram type when present.</summary>
    public static string Summary(string hex)
    {
        var (tags, issues) = Parse(hex);
        var sb = new StringBuilder($"{tags.Count} EMV data object{(tags.Count == 1 ? "" : "s")}");
        if (tags.FirstOrDefault(t => t.Tag == "9F27") is { Value.Length: 2 } cid)
        {
            var type = (Convert.ToByte(cid.Value, 16) & 0xC0) switch { 0x00 => "AAC (declined offline)", 0x40 => "TC (approved offline)", 0x80 => "ARQC (asks the issuer to decide)", _ => "reserved" };
            sb.Append($"; cryptogram type {type}");
        }
        if (issues.Count > 0) sb.Append("; ").Append(issues[0]);
        return sb.ToString();
    }
}
