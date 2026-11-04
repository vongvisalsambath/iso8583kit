using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Iso8583Kit;
using Iso8583Kit.Cli;

try
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help") return Help();
    var (command, positional, options, fields) = Args.Parse(args);
    var encoding = options.GetValueOrDefault("encoding") switch
    {
        null or "ascii" => Iso8583Encoding.Ascii,
        "ascii-binary" => Iso8583Encoding.AsciiBinaryBitmap,
        "bcd" => Iso8583Encoding.BcdBinary,
        var e => throw new UsageException($"--encoding is ascii, ascii-binary or bcd, not {Show.Text(e)}."),
    };
    return command switch
    {
        "parse" => Parse(positional, options, encoding),
        "build" => Build(options, fields, encoding),
        "host" => await Host(options, encoding),
        "send" => await Send(positional, options, fields, encoding),
        _ => throw new UsageException($"Unknown command {Show.Text(command)}."),
    };
}
catch (UsageException e)
{
    Console.Error.WriteLine("error: " + e.Message);
    Console.Error.WriteLine("Run 'iso8583 --help' for the commands.");
    return 2;
}
catch (Iso8583Exception e)
{
    foreach (var p in e.Problems) Console.Error.WriteLine("refused: " + p.Message);
    return 1;
}

static int Parse(List<string> positional, Dictionary<string, string> options, Iso8583Encoding encoding)
{
    if (positional.Count != 1) throw new UsageException("'parse' takes one file (or - for standard input).");
    var bytes = Input.Read(positional[0]);
    var result = options.ContainsKey("hex")
        ? Iso8583Codec.UnpackHex(Encoding.ASCII.GetString(bytes), encoding: encoding)
        : Iso8583Codec.Unpack(bytes, encoding: encoding);
    var issues = result.Issues.Concat(result.Message is null ? [] : Rules.Check(result.Message, now: DateTime.UtcNow)).ToList();
    if (options.ContainsKey("json")) Console.WriteLine(Report.Json(result.Message, issues));
    else Console.Write(Report.Text(result.Message, issues));
    return issues.Any(i => i.IsError) || result.Message is null ? 1 : 0;
}

static int Build(Dictionary<string, string> options, Dictionary<int, string> fields, Iso8583Encoding encoding)
{
    var message = Message(options, fields);
    var bytes = Iso8583Codec.Pack(message, encoding: encoding);
    Console.WriteLine(Convert.ToHexString(bytes));
    foreach (var i in Rules.Check(message, now: DateTime.UtcNow)) Console.Error.WriteLine($"{(i.IsError ? "error" : "warning")} {i.Code}: {i.Message}");
    return 0;
}

static Iso8583Message Message(Dictionary<string, string> options, Dictionary<int, string> fields)
{
    var mti = options.GetValueOrDefault("mti") ?? throw new UsageException("Give the message type with --mti, such as --mti 0200.");
    var m = new Iso8583Message(mti);
    foreach (var (n, v) in fields) m[n] = v;
    return m;
}

static async Task<int> Host(Dictionary<string, string> options, Iso8583Encoding encoding)
{
    var port = Args.Int(options, "port", 8583, 1, 65535);
    var bindText = options.GetValueOrDefault("bind") ?? "127.0.0.1";
    if (!IPAddress.TryParse(bindText, out var bind)) throw new UsageException($"--bind is an IP address, not {Show.Text(bindText)}.");
    if (!IPAddress.IsLoopback(bind) && !options.ContainsKey("allow-remote"))
        throw new UsageException("The simulator only listens on this computer unless you add --allow-remote: it approves anything that looks right, so don't expose it.");
    var hostOptions = new HostOptions
    {
        ApproveUpTo = Args.Int(options, "limit", 100_000, 0, int.MaxValue),
        ResponseFromCents = !options.ContainsKey("no-test-amounts"),
    };
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    var server = new HostServer(bind, port, encoding, hostOptions, Console.Out);
    await server.RunAsync(stop.Token);
    return 0;
}

static async Task<int> Send(List<string> positional, Dictionary<string, string> options, Dictionary<int, string> fields, Iso8583Encoding encoding)
{
    if (positional.Count != 1) throw new UsageException("'send' takes the host as host:port, such as 127.0.0.1:8583.");
    var target = positional[0];
    var colon = target.LastIndexOf(':');
    if (colon <= 0 || !int.TryParse(target[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        throw new UsageException("Give the host as host:port, such as 127.0.0.1:8583.");
    var message = Message(options, fields);
    var bytes = Iso8583Codec.Pack(message, encoding: encoding);
    using var client = new TcpClient();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Args.Int(options, "timeout", 10, 1, 120)));
    try
    {
        await client.ConnectAsync(target[..colon], port, timeout.Token);
        var stream = client.GetStream();
        await Framing.WriteAsync(stream, bytes, timeout.Token);
        Console.WriteLine("sent:");
        Console.Write(Report.Text(message, []));
        var reply = await Framing.ReadAsync(stream, timeout.Token) ?? throw new IOException("The host closed the connection without answering.");
        var result = Iso8583Codec.Unpack(reply, encoding: encoding);
        Console.WriteLine("received:");
        Console.Write(Report.Text(result.Message, result.Issues));
        return result.Message?[39] == "00" ? 0 : 1;
    }
    catch (Exception e) when (e is SocketException or IOException or OperationCanceledException)
    {
        Console.Error.WriteLine(e is OperationCanceledException ? "The host didn't answer in time." : $"Couldn't talk to {Show.Text(target)}: {Show.Text(e.Message)}");
        return 2;
    }
}

static int Help()
{
    Console.WriteLine("""
        iso8583: read, build and send ISO 8583 (1987) card messages, and simulate a card host.

          iso8583 parse FILE [--hex] [--json]           explain a message (FILE as bytes, or as hexadecimal with --hex; - for stdin)
          iso8583 build --mti 0200 --field 2=... ...    write a message as hexadecimal
          iso8583 host [--port 8583] [--limit CENTS]    answer requests on 127.0.0.1 (see README for test amounts)
          iso8583 send HOST:PORT --mti 0200 --field ... send a message and show the answer

          --encoding ascii | ascii-binary | bcd         how the message is laid out (default ascii)

        Card numbers are shown as their first 6 and last 4 digits; track data, PIN blocks and MACs are never shown.
        Exit codes: 0 done (send: approved), 1 refused, invalid or declined, 2 usage, file or network error.
        """);
    return 0;
}

namespace Iso8583Kit.Cli
{
    internal sealed class UsageException(string message) : Exception(message);

    internal static class Args
    {
        private static readonly HashSet<string> Flags = ["hex", "json", "allow-remote", "no-test-amounts"];
        private static readonly HashSet<string> Valued = ["encoding", "mti", "port", "bind", "limit", "timeout", "field"];

        public static (string, List<string>, Dictionary<string, string>, Dictionary<int, string>) Parse(string[] args)
        {
            var positional = new List<string>();
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var fields = new SortedDictionary<int, string>();
            for (var i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (!a.StartsWith("--", StringComparison.Ordinal) || a == "-") { positional.Add(a); continue; }
                var name = a[2..];
                if (Flags.Contains(name)) { options[name] = ""; continue; }
                if (!Valued.Contains(name)) throw new UsageException($"Unknown option {Show.Text(a)}.");
                if (i + 1 >= args.Length) throw new UsageException($"{a} needs a value.");
                var value = args[++i];
                if (name == "field")
                {
                    var eq = value.IndexOf('=');
                    if (eq <= 0 || !int.TryParse(value[..eq], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 2 or > 128)
                        throw new UsageException("--field is NUMBER=VALUE, with a number from 2 to 128, such as --field 4=000000002500.");
                    if (!fields.TryAdd(n, value[(eq + 1)..])) throw new UsageException($"Field {n} is given twice.");
                }
                else if (!options.TryAdd(name, value)) throw new UsageException($"{a} is given twice.");
            }
            return (args[0], positional, options, new Dictionary<int, string>(fields));
        }

        public static int Int(Dictionary<string, string> options, string name, int fallback, int min, int max)
        {
            if (!options.TryGetValue(name, out var s)) return fallback;
            return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
                ? v : throw new UsageException($"--{name} is a whole number from {min} to {max}.");
        }
    }

    internal static class Input
    {
        public static byte[] Read(string path)
        {
            const int limit = Iso8583Codec.MaxBytes * 2 + 4096; // room for hexadecimal with spaces
            try
            {
                using var stream = path == "-" ? Console.OpenStandardInput() : File.OpenRead(path);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int read;
                while ((read = stream.Read(chunk)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > limit) throw new UsageException("The input is larger than any ISO 8583 message.");
                }
                return buffer.ToArray();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new UsageException($"Can't read {Show.Text(path)}.");
            }
        }
    }

    /// <summary>Text from a message or the command line, safe to print: printable ASCII only, cut to a length.</summary>
    internal static class Show
    {
        public static string Text(string? s, int max = 80) =>
            s is null ? "" : new string(s.Take(max).Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray()) + (s.Length > max ? "…" : "");
    }

    /// <summary>How messages are framed on a TCP connection: a 2-byte big-endian length, then the message.</summary>
    internal static class Framing
    {
        public static async Task WriteAsync(Stream stream, byte[] message, CancellationToken ct)
        {
            var header = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)message.Length);
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(message, ct);
        }

        /// <summary>The next message, or null when the connection closed cleanly. Throws IOException for a frame over the size limit.</summary>
        public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct)
        {
            var header = new byte[2];
            if (!await Fill(stream, header, ct)) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(header);
            if (length == 0 || length > Iso8583Codec.MaxBytes) throw new IOException($"A frame of {length} bytes is outside the allowed 1 to {Iso8583Codec.MaxBytes}.");
            var body = new byte[length];
            if (!await Fill(stream, body, ct)) throw new IOException("The connection closed in the middle of a message.");
            return body;
        }

        private static async Task<bool> Fill(Stream stream, byte[] buffer, CancellationToken ct)
        {
            var got = 0;
            while (got < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(got), ct);
                if (n == 0) return got == 0 ? false : throw new IOException("The connection closed in the middle of a message.");
                got += n;
            }
            return true;
        }
    }

    /// <summary>A simulated host on TCP. Every logged value goes through the masking rules.</summary>
    internal sealed class HostServer(IPAddress bind, int port, Iso8583Encoding encoding, HostOptions options, TextWriter log)
    {
        public const int MaxConnections = 64;
        public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
        private int connections;

        public int Port { get; private set; } = port;

        public async Task RunAsync(CancellationToken stop, TaskCompletionSource<int>? started = null)
        {
            var listener = new TcpListener(bind, Port);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            log.WriteLine($"Simulated host on {bind}:{Port} ({encoding.Name}); approving up to {options.ApproveUpTo} minor units. Ctrl+C to stop.");
            started?.TrySetResult(Port);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop);
                    if (Interlocked.Increment(ref connections) > MaxConnections)
                    {
                        Interlocked.Decrement(ref connections);
                        client.Dispose();
                        lock (log) log.WriteLine("refused a connection: too many open");
                        continue;
                    }
                    _ = Task.Run(() => Serve(client, stop), stop);
                }
            }
            catch (OperationCanceledException) { }
            finally { listener.Stop(); }
        }

        private async Task Serve(TcpClient client, CancellationToken stop)
        {
            var who = client.Client.RemoteEndPoint?.ToString() ?? "?";
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    while (!stop.IsCancellationRequested)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop);
                        idle.CancelAfter(IdleTimeout);
                        var frame = await Framing.ReadAsync(stream, idle.Token);
                        if (frame is null) break;
                        var request = Iso8583Codec.Unpack(frame, encoding: encoding);
                        if (request.Message is null)
                        {
                            lock (log) log.WriteLine($"{who}: unreadable message: {request.Issues[0].Message}");
                            break; // the stream can't be trusted after a bad frame
                        }
                        var answer = Simulator.Respond(request.Message, options);
                        lock (log) log.WriteLine($"{who}: {request.Message.Mti} {Summary(request.Message)}");
                        if (answer is null) continue;
                        var (response, why) = answer.Value;
                        await Framing.WriteAsync(stream, Iso8583Codec.Pack(response, encoding: encoding), idle.Token);
                        lock (log) log.WriteLine($"{who}: {response.Mti} {response[39]} ({why})");
                    }
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or Iso8583Exception)
            {
                lock (log) log.WriteLine($"{who}: closed ({(e is OperationCanceledException ? "idle" : Show.Text(e.Message))})");
            }
            finally
            {
                Interlocked.Decrement(ref connections);
            }
        }

        private static string Summary(Iso8583Message m)
        {
            var spec = Iso8583Spec.Default1987;
            var parts = new List<string>();
            if (m[2] is { } pan) parts.Add("card " + Rules.Display(spec[2], pan));
            if (m[4] is { } amount) parts.Add(Explain.Amount(amount, m[49]));
            if (m[11] is { } stan) parts.Add("STAN " + Show.Text(stan));
            return string.Join(", ", parts);
        }
    }

    /// <summary>Messages as text or JSON, with every value masked by its field's sensitivity.</summary>
    internal static class Report
    {
        public static string Text(Iso8583Message? m, IReadOnlyList<Iso8583Issue> issues)
        {
            var sb = new StringBuilder();
            var spec = Iso8583Spec.Default1987;
            if (m is not null)
            {
                sb.AppendLine($"MTI {Show.Text(m.Mti)}: {Explain.Mti(m.Mti)}");
                foreach (var (n, v) in m.Fields)
                {
                    var def = spec[n];
                    var meaning = def?.Sensitivity == Sensitivity.None ? Explain.Field(n, v, m) : n == 2 ? Explain.Field(n, v, m) : null;
                    sb.AppendLine($"  {n,3} {Show.Text(def?.Name ?? "(not defined)", 45),-45} {Rules.Display(def, v)}{(meaning is null ? "" : "  = " + Show.Text(meaning, 120))}");
                    if (n == 55 && def is not null)
                        foreach (var t in Emv.Parse(v).Tags) sb.AppendLine($"        {t.Tag,-5} {t.Name,-40} {t.Value}");
                }
            }
            foreach (var i in issues) sb.AppendLine($"{(i.IsError ? "error" : "warning")} {i.Code}{(i.Field is int f ? $" (field {f})" : "")}: {Show.Text(i.Message, 300)}");
            return sb.ToString();
        }

        public static string Json(Iso8583Message? m, IReadOnlyList<Iso8583Issue> issues)
        {
            var spec = Iso8583Spec.Default1987;
            return JsonSerializer.Serialize(new
            {
                valid = m is not null && issues.All(i => !i.IsError),
                mti = m?.Mti,
                meaning = m is null ? null : Explain.Mti(m.Mti),
                fields = m?.Fields.Select(f => new
                {
                    number = f.Key,
                    name = spec[f.Key]?.Name,
                    value = Rules.Display(spec[f.Key], f.Value),
                    meaning = spec[f.Key]?.Sensitivity is Sensitivity.None or Sensitivity.Pan ? Explain.Field(f.Key, f.Value, m) : null,
                }),
                issues,
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
    }
}
