using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace TokenBar.Core;

/// <summary>Wire layer for the Discord Rich Presence feature: framing codec,
/// JSON serialization, and the pipe the production client connects to. Port
/// of the static half of macOS <c>DiscordIPC.swift</c>.
///
/// <see cref="DiscordPresence.Payload.Fields"/> is the user-derived published
/// surface. This file may rename a key on the way out; it adds exactly four
/// leaves of its own, none derived from the user or the machine: the pid, a
/// bare GUID nonce, and one button's label and URL. A fifth leaf, or any of
/// these four depending on the machine, is what this sentence forbids
/// (DiscordIPC.swift :14-29).
/// <c>DiscordPresenceTests.ActivityFrameCarriesExactlyThePinnedKeysAndLeaves</c>
/// walks the serialized bytes (its <c>Walk</c> helper) to hold that
/// line.</summary>
public static class DiscordIpc
{
    /// <summary>Public constant, not a secret: visible to anyone who sees the
    /// presence. The client secret and bot token this feature does not need
    /// must never enter the repository.</summary>
    public const string ApplicationId = "1534085299163107348";

    /// <summary>Discord documents no frame limit, so this is ours: about 50x
    /// the largest frame this client can provoke, checked before any
    /// allocation sized from the wire (DiscordIPC.swift :36-41).</summary>
    public const uint MaxFrameLength = 64 * 1024;

    /// <summary>Transport constants, deliberately not payload fields, so they
    /// are pinned by literal assertions rather than admitted by the payload's
    /// own expected value (DiscordIPC.swift :43-106). Nothing may be appended
    /// to either — no query, no fragment, nothing from the machine.</summary>
    public const string ButtonLabel = "View on GitHub";

    public const string ButtonUrl = "https://github.com/Nanako0129/Syrtis-Windows";

    public const string ReadyEvent = "ready";

    public enum Opcode : uint
    {
        Handshake = 0,
        Frame = 1,
        Close = 2,
        Ping = 3,
        Pong = 4,
    }

    /// <summary>Whether a publish invalidates work computed before it
    /// (DiscordIPC.swift :108-143). A reduction or a retirement bumps the
    /// client's epoch, so a payload computed before the user narrowed what is
    /// shown is never written after it.</summary>
    public readonly record struct VisibilityChange(bool Retires)
    {
        /// <summary>An ordinary sample built from the current state.</summary>
        public static VisibilityChange None => new(false);

        /// <summary>The user took something off the profile.</summary>
        public static VisibilityChange Reducing => new(true);

        /// <summary>The user put something back. Nothing earlier becomes
        /// wrong.</summary>
        public static VisibilityChange Increasing => new(false);

        /// <summary>The published content was replaced (another client
        /// selected).</summary>
        public static VisibilityChange Retiring => new(true);

        /// <summary>Union of two changes landing together: losing a retire
        /// would let stale work reach the pipe.</summary>
        public VisibilityChange Combined(VisibilityChange other) => new(Retires || other.Retires);
    }

    // ── Framing ──────────────────────────────────────────────────────────

    /// <summary>4-byte little-endian opcode, 4-byte little-endian length, then
    /// the body.</summary>
    public static byte[] Encode(Opcode opcode, ReadOnlySpan<byte> body)
    {
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)body.Length);
        body.CopyTo(frame.AsSpan(8));
        return frame;
    }

    public enum DecodeResult
    {
        /// <summary>Not a whole frame yet; nothing consumed.</summary>
        NeedMore,

        /// <summary>A complete frame with a known opcode and a JSON body.</summary>
        Frame,

        /// <summary>Consumed and dropped (unknown opcode, or a body that is not
        /// JSON). Silent by design: nothing from a frame is logged.</summary>
        Discard,

        /// <summary>The stream is not trustworthy; disconnect.</summary>
        Fatal,
    }

    /// <summary>Decode one frame from the front of <paramref name="buffer"/>.
    /// The length bound is checked BEFORE the completeness check and before
    /// any allocation: a 4 GiB length is refused outright, not waited on
    /// (DiscordIPC.swift :179-199).</summary>
    public static DecodeResult Decode(
        ReadOnlySpan<byte> buffer, out int consumed, out Opcode opcode, out byte[] body)
    {
        consumed = 0;
        opcode = default;
        body = [];
        if (buffer.Length < 8)
        {
            return DecodeResult.NeedMore;
        }

        var rawOpcode = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        if (length > MaxFrameLength)
        {
            return DecodeResult.Fatal;
        }

        var total = 8 + (int)length;
        if (buffer.Length < total)
        {
            return DecodeResult.NeedMore;
        }

        consumed = total;
        if (!Enum.IsDefined((Opcode)rawOpcode))
        {
            return DecodeResult.Discard;
        }

        // Allocated only now: the length is already bounded above.
        var candidate = buffer[8..total].ToArray();
        try
        {
            // Object or array only, like Foundation's JSONSerialization
            // default; JsonDocument also rejects invalid UTF-8 and trailing
            // content.
            using var document = JsonDocument.Parse(candidate);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                return DecodeResult.Discard;
            }
        }
        catch (JsonException)
        {
            return DecodeResult.Discard;
        }

        opcode = (Opcode)rawOpcode;
        body = candidate;
        return DecodeResult.Frame;
    }

    // ── Serialization ────────────────────────────────────────────────────

    public static byte[] HandshakeJson() => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("client_id", ApplicationId);
        writer.WriteNumber("v", 1);
        writer.WriteEndObject();
    });

    /// <summary>Map the user-derived surface onto Discord's activity wire
    /// shape and add the transport's constant leaves beside it. A null
    /// payload is the clear, <c>"activity":null</c>, carrying nothing —
    /// buttons included (DiscordIPC.swift :214-246).</summary>
    public static byte[] ActivityJson(DiscordPresence.Payload? payload, int pid, string nonce) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("args");
            writer.WritePropertyName("activity");
            if (payload is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                foreach (var (key, value) in payload.Fields)
                {
                    // Renaming is allowed, adding is not.
                    if (key == "largeImageKey")
                    {
                        writer.WriteStartObject("assets");
                        writer.WriteString("large_image", value);
                        writer.WriteEndObject();
                    }
                    else
                    {
                        writer.WriteString(key, value);
                    }
                }

                writer.WriteStartArray("buttons");
                writer.WriteStartObject();
                writer.WriteString("label", ButtonLabel);
                writer.WriteString("url", ButtonUrl);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteNumber("pid", pid);
            writer.WriteEndObject();
            writer.WriteString("cmd", "SET_ACTIVITY");
            writer.WriteString("nonce", nonce);
            writer.WriteEndObject();
        });

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            body(writer);
        }

        return stream.ToArray();
    }

    /// <summary>The only thing ever read out of an inbound frame, and the only
    /// string it can produce. A READY frame carries the Discord account's
    /// username, id and avatar; none of it may enter this process's state or
    /// logs, so a fixed token is returned rather than the parsed object
    /// (DiscordIPC.swift :310-319).</summary>
    public static string Inbound(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("evt", out var evt)
                && evt.ValueKind == JsonValueKind.String
                && evt.ValueEquals("READY")
                ? ReadyEvent
                : "other";
        }
        catch (JsonException)
        {
            return "other";
        }
    }

    // ── The pipe ─────────────────────────────────────────────────────────

    /// <summary>Everything that decides which pipe a client opens and with
    /// what security, in one value so a test can pin the production values
    /// without connecting, while the fake-server tests go through the SAME
    /// construction code with only <see cref="Name"/> changed.</summary>
    public sealed record PipeSpec(
        string Server,
        string Name,
        PipeOptions Options,
        TokenImpersonationLevel Impersonation,
        HandleInheritability Inheritability);

    /// <summary>The production pipe. Local server "." only. Only
    /// <c>discord-ipc-0</c>: probing 1..9 widens the set of endpoints a
    /// same-user process can squat on for no user-visible gain (user decision
    /// 2026-09-25, macOS parity). Identification lets the server learn who we
    /// are but never act as us; CurrentUserOnly refuses a pipe another account
    /// created; the handle is not inheritable, so a helper process the Rust
    /// core spawns cannot keep the connection alive after teardown
    /// (DiscordIPC.swift :341-357). How .NET applies the first two on Unix
    /// sockets is NOT established by the macOS test runs; the Windows
    /// behaviour is measured on x64.</summary>
    public static readonly PipeSpec ProductionPipe = new(
        ".",
        "discord-ipc-0",
        PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous,
        TokenImpersonationLevel.Identification,
        HandleInheritability.None);

    /// <summary>The exact arguments <see cref="Connector"/> passes to the
    /// <see cref="NamedPipeClientStream"/> constructor, in its parameter
    /// order. Pure, so a test can pin what the production spec turns into
    /// without creating or connecting anything.</summary>
    public static (string Server, string Name, PipeDirection Direction, PipeOptions Options,
        TokenImpersonationLevel Impersonation, HandleInheritability Inheritability)
        StreamArgs(PipeSpec spec) =>
        (spec.Server, spec.Name, PipeDirection.InOut, spec.Options, spec.Impersonation, spec.Inheritability);

    /// <summary>Opens a connection to <paramref name="spec"/>, constructed
    /// from <see cref="StreamArgs"/> verbatim. The connect timeout arrives from
    /// the client (1 s in production) so a pipe that never accepts cannot park
    /// the worker.</summary>
    public static Func<TimeSpan, CancellationToken, Task<Stream>> Connector(PipeSpec spec) =>
        async (timeout, cancellationToken) =>
        {
            var args = StreamArgs(spec);
            var pipe = new NamedPipeClientStream(
                args.Server, args.Name, args.Direction, args.Options,
                args.Impersonation, args.Inheritability);
            try
            {
                await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken)
                    .ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        };

    public static Func<TimeSpan, CancellationToken, Task<Stream>> ProductionConnector { get; } =
        Connector(ProductionPipe);
}
