using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Iso8583Kit.Cli;
using Xunit;

namespace Iso8583Kit.Tests;

/// <summary>The simulated host over real TCP connections.</summary>
public sealed class HostTests : IAsyncLifetime
{
    private readonly CancellationTokenSource stop = new();
    private readonly StringWriter log = new();
    private int port;

    public async Task InitializeAsync()
    {
        var started = new TaskCompletionSource<int>();
        var server = new HostServer(IPAddress.Loopback, 0, Iso8583Encoding.Ascii, new HostOptions(), TextWriter.Synchronized(log));
        _ = server.RunAsync(stop.Token, started);
        port = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public Task DisposeAsync()
    {
        stop.Cancel();
        return Task.CompletedTask;
    }

    private async Task<NetworkStream> Connect()
    {
        var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, port);
        return c.GetStream();
    }

    [Fact]
    public async Task ARequestGetsAResponse_AndTheLogShowsNoCardNumber()
    {
        var stream = await Connect();
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = CodecTests.Purchase();
        await Framing.WriteAsync(stream, Iso8583Codec.Pack(request), ct.Token);
        var reply = Iso8583Codec.Unpack((await Framing.ReadAsync(stream, ct.Token))!);
        Assert.Equal(("0210", "00", "000123"), (reply.Message!.Mti, reply.Message[39], reply.Message[11]));
        // a second message on the same connection
        await Framing.WriteAsync(stream, Iso8583Codec.Pack(new Iso8583Message("0800") { [11] = "000002", [70] = "301" }), ct.Token);
        Assert.Equal("0810", Iso8583Codec.Unpack((await Framing.ReadAsync(stream, ct.Token))!).Message!.Mti);
        await Task.Delay(200);
        var text = log.ToString();
        Assert.Contains("411111******1111", text);
        Assert.DoesNotContain(CodecTests.Visa, text);
        Assert.DoesNotContain("28121010000012300000", text); // track 2's tail
    }

    [Theory]
    [InlineData(60000)]
    [InlineData(0)]
    public async Task AnOutOfRangeFrameClosesTheConnection(int length)
    {
        var stream = await Connect();
        var header = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)length);
        await stream.WriteAsync(header);
        await stream.WriteAsync("0200"u8.ToArray());
        var buffer = new byte[16];
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await stream.ReadAsync(buffer, ct.Token));
    }

    [Fact]
    public async Task AnUnreadableMessageClosesTheConnection()
    {
        var stream = await Connect();
        var body = new byte[] { 0x1B, 0x5B, 0x32, 0x4A, 0x00, 0xFF };
        await stream.WriteAsync(new byte[] { 0, (byte)body.Length });
        await stream.WriteAsync(body);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await stream.ReadAsync(new byte[16], ct.Token));
        await Task.Delay(200);
        Assert.DoesNotContain('\u001B', log.ToString());
    }

    [Fact]
    public async Task ConnectionsBeyondTheLimitAreRefused()
    {
        var open = new List<NetworkStream>();
        for (var i = 0; i < HostServer.MaxConnections + 10; i++) open.Add(await Connect());
        await Task.Delay(500);
        var closed = 0;
        foreach (var s in open)
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try { if (await s.ReadAsync(new byte[1], ct.Token) == 0) closed++; }
            catch (OperationCanceledException) { }
            catch (IOException) { closed++; }
        }
        Assert.True(closed >= 10, $"{closed} closed");
        foreach (var s in open) s.Dispose();
    }
}
