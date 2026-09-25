using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Opcode = TokenBar.Core.DiscordIpc.Opcode;
using VisibilityChange = TokenBar.Core.DiscordIpc.VisibilityChange;

namespace TokenBar.Core.Tests;

/// <summary>A stand-in Discord on a RANDOM per-test pipe name, reached through
/// the client's connection seam with the production pipe spec's options.
///
/// No test may create or connect to <c>discord-ipc-0</c>: CI runs this suite on
/// windows-latest, and a developer or x64 machine may have Discord running, so
/// a stray fixture publish would reach a real profile. <see cref="Connector"/>
/// refuses the production name outright rather than trusting every caller.
///
/// What this proves on macOS is framing and lifecycle over .NET's Unix-socket
/// pipe emulation. It proves nothing about how Windows applies
/// Identification or CurrentUserOnly; that is measured on x64.</summary>
internal sealed class FakeDiscord : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<NamedPipeServerStream> _accepted = Channel.CreateUnbounded<NamedPipeServerStream>();
    private readonly List<NamedPipeServerStream> _all = [];
    private int _acceptCount;

    // Short: on macOS the pipe becomes a Unix socket under $TMPDIR, and
    // sun_path allows 104 characters (measured: a 32-hex suffix after a
    // 16-character prefix overflowed it).
    public string Name { get; } = "tbdc-" + Guid.NewGuid().ToString("N")[..16];

    public int AcceptCount => Volatile.Read(ref _acceptCount);

    public FakeDiscord() => _ = Task.Run(AcceptLoopAsync);

    public Func<TimeSpan, CancellationToken, Task<Stream>> Connector()
    {
        var spec = DiscordIpc.ProductionPipe with { Name = Name };
        // Guarded on what the connector will actually open (StreamArgs is what
        // Connector constructs from), not only on the spec: a StreamArgs that
        // ignored the spec's name would otherwise send every fixture to the
        // real Discord pipe on a Windows machine.
        Assert.NotEqual(DiscordIpc.ProductionPipe.Name, DiscordIpc.StreamArgs(spec).Name);
        return DiscordIpc.Connector(spec);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            lock (_all)
            {
                _all.Add(server);
            }

            try
            {
                await server.WaitForConnectionAsync(_stop.Token);
            }
            catch
            {
                return;
            }

            Interlocked.Increment(ref _acceptCount);
            _accepted.Writer.TryWrite(server);
        }
    }

    public async Task<NamedPipeServerStream> NextConnectionAsync(int timeoutMs = 5_000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        return await _accepted.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>Accept, read the handshake, answer READY.</summary>
    public async Task<NamedPipeServerStream> HandshakeAsync()
    {
        var connection = await NextConnectionAsync();
        var (opcode, body) = await ReadFrameAsync(connection);
        Assert.Equal(Opcode.Handshake, opcode);
        Assert.Contains("1534085299163107348", Encoding.UTF8.GetString(body));
        await SendAsync(connection, Opcode.Frame,
            """{"cmd":"DISPATCH","evt":"READY","data":{"user":{"username":"secret-user","id":"42"}}}""");
        return connection;
    }

    public static async Task SendAsync(Stream stream, Opcode opcode, string json) =>
        await SendRawAsync(stream, DiscordIpc.Encode(opcode, Encoding.UTF8.GetBytes(json)));

    public static async Task SendRawAsync(Stream stream, byte[] bytes)
    {
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    /// <summary>Null on EOF.</summary>
    public static async Task<(Opcode Opcode, byte[] Body)> ReadFrameAsync(Stream stream, int timeoutMs = 5_000)
    {
        var frame = await TryReadFrameAsync(stream, timeoutMs);
        Assert.True(frame is not null, "expected a frame, got EOF");
        return frame.Value;
    }

    public static async Task<(Opcode Opcode, byte[] Body)?> TryReadFrameAsync(Stream stream, int timeoutMs = 5_000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        var header = new byte[8];
        if (!await ReadExactlyOrEofAsync(stream, header, timeout.Token))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        Assert.True(length <= DiscordIpc.MaxFrameLength);
        var body = new byte[length];
        Assert.True(await ReadExactlyOrEofAsync(stream, body, timeout.Token), "EOF mid-frame");
        return ((Opcode)BinaryPrimitives.ReadUInt32LittleEndian(header), body);
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    /// <summary>The <c>activity</c> element of a SET_ACTIVITY frame, as raw
    /// JSON ("null" for the clear).</summary>
    public static string Activity(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.Equal("SET_ACTIVITY", document.RootElement.GetProperty("cmd").GetString());
        return document.RootElement.GetProperty("args").GetProperty("activity").GetRawText();
    }

    public static async Task<string> NextActivityAsync(Stream stream, int timeoutMs = 5_000)
    {
        var (opcode, body) = await ReadFrameAsync(stream, timeoutMs);
        Assert.Equal(Opcode.Frame, opcode);
        return Activity(body);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        lock (_all)
        {
            foreach (var server in _all)
            {
                server.Dispose();
            }
        }
    }
}

// F3: framing, the production pipe values, and the client lifecycle against a
// fake server. Timings are shrunk through the injectable Timing; the
// production values are pinned separately.
public sealed class DiscordIpcClientTests
{
    private static DiscordPresence.Payload P(string details) => new(details, "", DiscordPresence.LargeImageKey);

    private static DiscordIpcClient.Timing Fast(TimeSpan? publishInterval = null) => new(
        publishInterval ?? TimeSpan.FromHours(1),
        ReconnectDelay: TimeSpan.FromMilliseconds(10),
        ReadyTimeout: TimeSpan.FromSeconds(5),
        ConnectTimeout: TimeSpan.FromMilliseconds(500),
        WriteTimeout: TimeSpan.FromSeconds(2));

    private static async Task Eventually(Func<Task<bool>> condition, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(10);
        }
    }

    private static async Task<(DiscordIpcClient Client, Stream Connection)> ReadyClientAsync(
        FakeDiscord server, DiscordIpcClient.Timing timing)
    {
        var client = new DiscordIpcClient(server.Connector(), timing);
        client.Start();
        var connection = await server.HandshakeAsync();
        await Eventually(client.IsReadyForTestingAsync);
        return (client, connection);
    }

    // ---- production values, pinned without connecting --------------------

    [Fact]
    public void ProductionPipeAndTimingArePinned()
    {
        var spec = DiscordIpc.ProductionPipe;
        Assert.Equal(".", spec.Server);
        Assert.Equal("discord-ipc-0", spec.Name);
        Assert.Equal(TokenImpersonationLevel.Identification, spec.Impersonation);
        Assert.Equal(PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous, spec.Options);
        Assert.Equal(HandleInheritability.None, spec.Inheritability);

        var timing = DiscordIpcClient.Production;
        Assert.Equal(TimeSpan.FromSeconds(15), timing.PublishInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), timing.ReconnectDelay);
        Assert.Equal(TimeSpan.FromSeconds(10), timing.ReadyTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), timing.ConnectTimeout);
        Assert.True(timing.WriteTimeout > TimeSpan.Zero && timing.WriteTimeout <= TimeSpan.FromSeconds(2));
        Assert.Equal(5, DiscordIpcClient.MaxReconnectAttempts);
        Assert.Equal(64u * 1024, DiscordIpc.MaxFrameLength);
    }

    [Fact]
    public void ProductionPipeMapsToThePinnedStreamArguments()
    {
        // What Connector hands NamedPipeClientStream for the production spec.
        // Pure: nothing is created or connected.
        var args = DiscordIpc.StreamArgs(DiscordIpc.ProductionPipe);

        Assert.Equal(".", args.Server);
        Assert.Equal("discord-ipc-0", args.Name);
        Assert.Equal(PipeDirection.InOut, args.Direction);
        Assert.Equal(PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous, args.Options);
        Assert.Equal(TokenImpersonationLevel.Identification, args.Impersonation);
        Assert.Equal(HandleInheritability.None, args.Inheritability);

        // Every field of the spec flows through; none is fixed in StreamArgs.
        var other = new DiscordIpc.PipeSpec(
            "srv", "tbdc-other", PipeOptions.None, TokenImpersonationLevel.Anonymous,
            HandleInheritability.Inheritable);
        Assert.Equal(
            ("srv", "tbdc-other", PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Anonymous,
                HandleInheritability.Inheritable),
            DiscordIpc.StreamArgs(other));
    }

    // ---- framing ------------------------------------------------------------

    private static byte[] Header(uint opcode, uint length)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header, opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), length);
        return header;
    }

    [Fact]
    public void DecodeRefusesOversizeLengthBeforeWaitingForTheBody()
    {
        // Header only: a bound checked after completeness would say NeedMore.
        Assert.Equal(DiscordIpc.DecodeResult.Fatal,
            DiscordIpc.Decode(Header(1, DiscordIpc.MaxFrameLength + 1), out _, out _, out _));
        Assert.Equal(DiscordIpc.DecodeResult.Fatal,
            DiscordIpc.Decode(Header(1, uint.MaxValue), out _, out _, out _));
        // Exactly at the bound is allowed, and merely incomplete.
        Assert.Equal(DiscordIpc.DecodeResult.NeedMore,
            DiscordIpc.Decode(Header(1, DiscordIpc.MaxFrameLength), out _, out _, out _));
    }

    [Fact]
    public void DecodeHandlesPartialBadOpcodeBadJsonAndGoodFrames()
    {
        Assert.Equal(DiscordIpc.DecodeResult.NeedMore, DiscordIpc.Decode(new byte[7], out _, out _, out _));

        var good = DiscordIpc.Encode(Opcode.Frame, Encoding.UTF8.GetBytes("""{"evt":"READY"}"""));
        Assert.Equal(DiscordIpc.DecodeResult.NeedMore, DiscordIpc.Decode(good[..^1], out var partial, out _, out _));
        Assert.Equal(0, partial);
        Assert.Equal(DiscordIpc.DecodeResult.Frame, DiscordIpc.Decode(good, out var consumed, out var opcode, out var body));
        Assert.Equal(good.Length, consumed);
        Assert.Equal(Opcode.Frame, opcode);
        Assert.Equal(DiscordIpc.ReadyEvent, DiscordIpc.Inbound(body));

        var badOpcode = DiscordIpc.Encode((Opcode)99, Encoding.UTF8.GetBytes("{}"));
        Assert.Equal(DiscordIpc.DecodeResult.Discard, DiscordIpc.Decode(badOpcode, out consumed, out _, out _));
        Assert.Equal(badOpcode.Length, consumed);

        foreach (var bad in new[] { "{not json", "42", "{} trailing" })
        {
            var frame = DiscordIpc.Encode(Opcode.Frame, Encoding.UTF8.GetBytes(bad));
            Assert.Equal(DiscordIpc.DecodeResult.Discard, DiscordIpc.Decode(frame, out consumed, out _, out _));
            Assert.Equal(frame.Length, consumed);
        }
    }

    [Fact]
    public void InboundReadsOnlyTheEventName()
    {
        Assert.Equal("ready", DiscordIpc.Inbound(Encoding.UTF8.GetBytes(
            """{"evt":"READY","data":{"user":{"username":"secret-user"}}}""")));
        Assert.Equal("other", DiscordIpc.Inbound(Encoding.UTF8.GetBytes("""{"evt":"ERROR"}""")));
        Assert.Equal("other", DiscordIpc.Inbound(Encoding.UTF8.GetBytes("""{"evt":1}""")));
        Assert.Equal("other", DiscordIpc.Inbound(Encoding.UTF8.GetBytes("[]")));
    }

    // ---- lifecycle against a fake server ---------------------------------

    [Fact]
    public async Task PublishReachesTheWireAfterReady()
    {
        await using var server = new FakeDiscord();
        var (client, connection) = await ReadyClientAsync(server, Fast());
        Assert.Equal("ready", await client.InboundTokenForTestingAsync());

        client.Publish(P("A"), VisibilityChange.None);

        Assert.Contains("\"details\":\"A\"", await FakeDiscord.NextActivityAsync(connection));
        client.Stop();
        Assert.Equal("null", await FakeDiscord.NextActivityAsync(connection));
        Assert.Null(await FakeDiscord.TryReadFrameAsync(connection));
    }

    [Fact]
    public async Task OversizeLengthDisconnects()
    {
        await using var server = new FakeDiscord();
        var (client, connection) = await ReadyClientAsync(server, Fast());

        await FakeDiscord.SendRawAsync(connection, Header(1, DiscordIpc.MaxFrameLength + 1));

        // The client closes its end: the server reads EOF.
        Assert.Null(await FakeDiscord.TryReadFrameAsync(connection));
        client.Stop();
    }

    [Fact]
    public async Task PingIsEchoedAndCloseDisconnects()
    {
        await using var server = new FakeDiscord();
        var (client, connection) = await ReadyClientAsync(server, Fast());

        await FakeDiscord.SendAsync(connection, Opcode.Ping, """{"n":7}""");
        var (opcode, body) = await FakeDiscord.ReadFrameAsync(connection);
        Assert.Equal(Opcode.Pong, opcode);
        Assert.Equal("""{"n":7}""", Encoding.UTF8.GetString(body));

        await FakeDiscord.SendAsync(connection, Opcode.Close, "{}");
        Assert.Null(await FakeDiscord.TryReadFrameAsync(connection));
        client.Stop();
    }

    [Fact]
    public async Task SilentServerGetsBoundedReconnects()
    {
        await using var server = new FakeDiscord();
        var timing = Fast() with { ReadyTimeout = TimeSpan.FromMilliseconds(50) };
        var client = new DiscordIpcClient(server.Connector(), timing);

        client.Start();

        await Eventually(client.IsAbandonedForTestingAsync);
        // One initial connection plus MaxReconnectAttempts, then nothing more.
        Assert.Equal(1 + DiscordIpcClient.MaxReconnectAttempts, server.AcceptCount);
        Assert.False(await client.IsConnectedForTestingAsync());
        await Task.Delay(200);
        Assert.Equal(1 + DiscordIpcClient.MaxReconnectAttempts, server.AcceptCount);
    }

    [Fact]
    public async Task UnreachablePipeGetsBoundedAttempts()
    {
        var attempts = 0;
        var client = new DiscordIpcClient(
            (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                throw new IOException("no pipe");
            },
            Fast());

        client.Start();

        await Eventually(client.IsAbandonedForTestingAsync);
        await Task.Delay(100);
        Assert.Equal(1 + DiscordIpcClient.MaxReconnectAttempts, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task DisableWithAQueuedPublishPutsOnlyTheClearOnTheWire()
    {
        await using var server = new FakeDiscord();
        var (client, connection) = await ReadyClientAsync(server, Fast());
        var gate = new TaskCompletionSource();

        client.HoldForTesting(gate.Task);
        client.Publish(P("queued-before-disable"), VisibilityChange.None);
        client.Stop();
        gate.SetResult();

        Assert.Equal("null", await FakeDiscord.NextActivityAsync(connection));
        Assert.Null(await FakeDiscord.TryReadFrameAsync(connection));
    }

    [Fact]
    public async Task DisableThenReenableNeverWritesAPayloadQueuedBeforeTheDisable()
    {
        // Start() restores `granted` off the worker before the queued A runs,
        // so only Stop()'s epoch bump keeps A — computed under the withdrawn
        // consent — off the wire (DiscordIPC.swift :438-446).
        await using var server = new FakeDiscord();
        var (client, first) = await ReadyClientAsync(server, Fast());
        var gate = new TaskCompletionSource();

        client.HoldForTesting(gate.Task);
        client.Publish(P("queued-before-disable"), VisibilityChange.None);
        client.Stop();
        client.Start();
        gate.SetResult();

        Assert.Equal("null", await FakeDiscord.NextActivityAsync(first));
        Assert.Null(await FakeDiscord.TryReadFrameAsync(first));

        // The re-enabled connection does not restore A either.
        var second = await server.HandshakeAsync();
        await Eventually(client.IsReadyForTestingAsync);
        client.Stop();
        Assert.Equal("null", await FakeDiscord.NextActivityAsync(second));
    }

    [Fact]
    public async Task ReconnectBudgetResetsOnReady()
    {
        // Four failures, then a connection that reaches READY and drops. With
        // the reset on READY the client gets a fresh budget of five; without
        // it, one more failure would exhaust the four already spent.
        await using var server = new FakeDiscord();
        var inner = server.Connector();
        var calls = 0;
        var client = new DiscordIpcClient(
            async (timeout, token) =>
            {
                if (Interlocked.Increment(ref calls) == 5)
                {
                    return await inner(timeout, token);
                }

                throw new IOException("not running");
            },
            Fast());

        client.Start();
        var connection = await server.HandshakeAsync();
        await Eventually(client.IsReadyForTestingAsync);
        connection.Dispose();

        await Eventually(client.IsAbandonedForTestingAsync);
        Assert.Equal(5 + DiscordIpcClient.MaxReconnectAttempts, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task ReducingPublishRetiresAPayloadQueuedAheadOfIt()
    {
        // Ready first, so A would be written at once if it ran, and B (new
        // information within the one-hour floor) would be deferred behind it.
        // Only the epoch keeps A off the wire.
        await using var server = new FakeDiscord();
        var (client, connection) = await ReadyClientAsync(server, Fast());
        var gate = new TaskCompletionSource();

        client.HoldForTesting(gate.Task);
        client.Publish(P("A"), VisibilityChange.None);
        client.Publish(P("B"), VisibilityChange.Reducing);
        gate.SetResult();

        Assert.Contains("\"details\":\"B\"", await FakeDiscord.NextActivityAsync(connection));
        client.Stop();
        Assert.Equal("null", await FakeDiscord.NextActivityAsync(connection));
        Assert.Null(await FakeDiscord.TryReadFrameAsync(connection));
    }

    [Fact]
    public async Task PayloadQueuedBeforeReadyThenReducingPayloadOnlyBReachesTheWire()
    {
        await using var server = new FakeDiscord();
        var client = new DiscordIpcClient(server.Connector(), Fast());
        client.Start();
        var connection = await server.NextConnectionAsync();
        Assert.Equal(Opcode.Handshake, (await FakeDiscord.ReadFrameAsync(connection)).Opcode);
        var gate = new TaskCompletionSource();

        client.HoldForTesting(gate.Task);
        client.Publish(P("A"), VisibilityChange.None);
        // READY lands while A is queued, so its restore would flush A...
        await FakeDiscord.SendAsync(connection, Opcode.Frame, """{"evt":"READY"}""");
        await Task.Delay(100);
        // ...unless the reduction behind it retires A first.
        client.Publish(P("B"), VisibilityChange.Reducing);
        gate.SetResult();

        Assert.Contains("\"details\":\"B\"", await FakeDiscord.NextActivityAsync(connection));
        client.Stop();
        Assert.Equal("null", await FakeDiscord.NextActivityAsync(connection));
        Assert.Null(await FakeDiscord.TryReadFrameAsync(connection));
    }

    [Fact]
    public async Task FloorDefersNewSamplesButNotTheClear()
    {
        await using var server = new FakeDiscord();
        var interval = TimeSpan.FromMilliseconds(400);
        var (client, connection) = await ReadyClientAsync(server, Fast(interval));

        client.Publish(P("A"), VisibilityChange.None);
        Assert.Contains("\"details\":\"A\"", await FakeDiscord.NextActivityAsync(connection));
        var sentA = DateTime.UtcNow;

        client.Publish(P("B"), VisibilityChange.None);
        Assert.Contains("\"details\":\"B\"", await FakeDiscord.NextActivityAsync(connection));
        Assert.True(DateTime.UtcNow - sentA >= interval - TimeSpan.FromMilliseconds(50),
            "a new sample went out inside the floor");

        // A clear carries no new information and skips the floor.
        var clearAt = DateTime.UtcNow;
        client.Publish(null, VisibilityChange.None);
        Assert.Equal("null", await FakeDiscord.NextActivityAsync(connection));
        Assert.True(DateTime.UtcNow - clearAt < interval, "the clear waited out the floor");
        client.Stop();
    }

    [Fact]
    public async Task UnchangedPayloadIsNotRepeated()
    {
        await using var server = new FakeDiscord();
        var (client, connection) = await ReadyClientAsync(server, Fast(TimeSpan.Zero));

        client.Publish(P("A"), VisibilityChange.None);
        client.Publish(P("A"), VisibilityChange.None);
        client.Publish(P("C"), VisibilityChange.None);

        Assert.Contains("\"details\":\"A\"", await FakeDiscord.NextActivityAsync(connection));
        Assert.Contains("\"details\":\"C\"", await FakeDiscord.NextActivityAsync(connection));
        client.Stop();
    }

    [Fact]
    public async Task ReconnectRestoresTheLastIntendedPayload()
    {
        await using var server = new FakeDiscord();
        var (client, first) = await ReadyClientAsync(server, Fast());
        client.Publish(P("A"), VisibilityChange.None);
        Assert.Contains("\"details\":\"A\"", await FakeDiscord.NextActivityAsync(first));

        // Discord restarts: the pipe drops, the client reconnects and restores
        // A without waiting out the floor (a restore is not a new sample).
        first.Dispose();
        var second = await server.HandshakeAsync();

        Assert.Contains("\"details\":\"A\"", await FakeDiscord.NextActivityAsync(second));
        client.Stop();
    }
}
