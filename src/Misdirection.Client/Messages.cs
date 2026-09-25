using System.Buffers.Binary;

namespace Misdirection.Client;

/// <summary>
/// A typed protocol message. Every message can be turned into a <see cref="Frame"/> and back with
/// <see cref="ToFrame"/> and <see cref="Decode"/>, so the same types serve both directions.
/// </summary>
public abstract record Message
{
    public abstract MessageType Type { get; }

    /// <summary>Payload bytes, little-endian per the spec.</summary>
    protected abstract byte[] EncodePayload();

    /// <summary>
    /// True for host-to-device message types (type &lt; 0x80). This is true for <see cref="DelayMessage"/>
    /// as well, so check <see cref="IsFileOnly"/> before putting a message on the wire.
    /// </summary>
    public bool IsHostToDevice => (byte)Type < 0x80;

    /// <summary>
    /// True for records that exist only inside <c>.msdr</c> files and must never be sent to the device
    /// (currently only <see cref="DelayMessage"/>). <see cref="MisdirectionClient.SendAsync(Message, CancellationToken)"/>
    /// rejects them.
    /// </summary>
    public bool IsFileOnly => Protocol.IsFileOnly(Type);

    public Frame ToFrame() => new(Type, EncodePayload());

    public byte[] ToBytes() => ToFrame().Encode();

    /// <summary>Decodes a frame into its typed message. Throws on a length mismatch or unknown type.</summary>
    public static Message Decode(Frame frame)
    {
        if (!TryDecode(frame, out var message, out var error))
            throw new ProtocolException(error);
        return message;
    }

    /// <summary>Decodes a frame into its typed message; returns false with a reason instead of throwing.</summary>
    public static bool TryDecode(Frame frame, out Message message, out string error)
    {
        var p = frame.Payload;
        message = null!;
        error = "";

        int expected = frame.Type switch
        {
            MessageType.Panic or MessageType.Ping => 0,
            MessageType.KeyDown or MessageType.KeyUp or MessageType.MouseButtons
                or MessageType.Pong or MessageType.Nack => 1,
            MessageType.MouseWheel => 2,
            MessageType.MouseMove or MessageType.ScreenSize or MessageType.FileDelay => 4,
            _ => -1,
        };

        if (expected < 0)
        {
            error = $"Unknown message type 0x{(byte)frame.Type:X2}.";
            return false;
        }
        if (p.Length != expected)
        {
            error = $"{frame.Type} expects a {expected}-byte payload, got {p.Length}.";
            return false;
        }

        message = frame.Type switch
        {
            MessageType.Panic => new PanicMessage(),
            MessageType.KeyDown => new KeyDownMessage(p[0]),
            MessageType.KeyUp => new KeyUpMessage(p[0]),
            MessageType.MouseMove => new MouseMoveMessage(
                BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt16LittleEndian(p[2..])),
            MessageType.MouseButtons => new MouseButtonsMessage((MouseButtons)p[0]),
            MessageType.MouseWheel => new MouseWheelMessage((sbyte)p[0], (sbyte)p[1]),
            MessageType.ScreenSize => new ScreenSizeMessage(
                BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt16LittleEndian(p[2..])),
            MessageType.Ping => new PingMessage(),
            MessageType.Pong => new PongMessage(p[0]),
            MessageType.Nack => new NackMessage((NackReason)p[0]),
            MessageType.FileDelay => new DelayMessage(BinaryPrimitives.ReadUInt32LittleEndian(p)),
            _ => throw new InvalidOperationException(),
        };
        return true;
    }

    private protected static byte[] U16Pair(ushort a, ushort b)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, a);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), b);
        return bytes;
    }
}

/// <summary>Release every key and button and reset firmware state. Honored even while disarmed.</summary>
public sealed record PanicMessage : Message
{
    public override MessageType Type => MessageType.Panic;
    protected override byte[] EncodePayload() => [];
}

/// <summary>Press a key. <paramref name="Usage"/> is an HID usage code (see <see cref="HidUsage"/>), not ASCII.</summary>
public sealed record KeyDownMessage(byte Usage) : Message
{
    public override MessageType Type => MessageType.KeyDown;
    protected override byte[] EncodePayload() => [Usage];
}

/// <summary>Release a key. A no-op in the firmware if the key is not held.</summary>
public sealed record KeyUpMessage(byte Usage) : Message
{
    public override MessageType Type => MessageType.KeyUp;
    protected override byte[] EncodePayload() => [Usage];
}

/// <summary>Move the pointer to an absolute position in screen pixels (see <see cref="ScreenSizeMessage"/>).</summary>
public sealed record MouseMoveMessage(ushort X, ushort Y) : Message
{
    public override MessageType Type => MessageType.MouseMove;
    protected override byte[] EncodePayload() => U16Pair(X, Y);
}

/// <summary>Set the absolute button state; the mask replaces whatever was held before.</summary>
public sealed record MouseButtonsMessage(MouseButtons Buttons) : Message
{
    public override MessageType Type => MessageType.MouseButtons;
    protected override byte[] EncodePayload() => [(byte)Buttons];
}

/// <summary>Scroll by signed detents. Positive vertical is up.</summary>
public sealed record MouseWheelMessage(sbyte Vertical, sbyte Horizontal = 0) : Message
{
    public override MessageType Type => MessageType.MouseWheel;
    protected override byte[] EncodePayload() => [(byte)Vertical, (byte)Horizontal];
}

/// <summary>
/// Tell the firmware the target's resolution so <see cref="MouseMoveMessage"/> coordinates land where
/// intended. Send at connect and on any resolution change. Honored even while disarmed.
/// </summary>
public sealed record ScreenSizeMessage(ushort Width, ushort Height) : Message
{
    public override MessageType Type => MessageType.ScreenSize;
    protected override byte[] EncodePayload() => U16Pair(Width, Height);
}

/// <summary>Liveness probe; the firmware answers with <see cref="PongMessage"/>.</summary>
public sealed record PingMessage : Message
{
    public override MessageType Type => MessageType.Ping;
    protected override byte[] EncodePayload() => [];
}

/// <summary>Reply to PING, also emitted unsolicited at firmware boot.</summary>
public sealed record PongMessage(byte Version) : Message
{
    public override MessageType Type => MessageType.Pong;
    protected override byte[] EncodePayload() => [Version];
}

/// <summary>Asynchronous error report from the firmware.</summary>
public sealed record NackMessage(NackReason Reason) : Message
{
    public override MessageType Type => MessageType.Nack;
    protected override byte[] EncodePayload() => [(byte)Reason];
}

/// <summary>
/// File-only record: the time that elapsed before the next frame in a <c>.msdr</c> file, in whole
/// microseconds. It borrows the frame encoding so a file stays a plain run of frames, but it is never
/// valid on the wire: the client refuses to send it, and a replayer waits instead of writing it.
/// Gaps longer than <see cref="uint.MaxValue"/> microseconds (about 71.6 minutes) are stored as several
/// consecutive records; see <see cref="ProtocolFileWriter.WriteDelay"/>.
/// </summary>
public sealed record DelayMessage(uint Microseconds) : Message
{
    public override MessageType Type => MessageType.FileDelay;

    /// <summary>The delay as a <see cref="TimeSpan"/>. Exact, since a tick is 100 ns.</summary>
    public TimeSpan Duration => TimeSpan.FromTicks(Microseconds * TimeSpan.TicksPerMicrosecond);

    protected override byte[] EncodePayload()
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Microseconds);
        return bytes;
    }
}

/// <summary>Thrown when bytes on the wire cannot be interpreted as a valid message.</summary>
public sealed class ProtocolException(string message) : Exception(message);
