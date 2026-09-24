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

    /// <summary>True for host-to-device messages (type &lt; 0x80).</summary>
    public bool IsHostToDevice => (byte)Type < 0x80;

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
            MessageType.MouseMove or MessageType.ScreenSize => 4,
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

/// <summary>Thrown when bytes on the wire cannot be interpreted as a valid message.</summary>
public sealed class ProtocolException(string message) : Exception(message);
