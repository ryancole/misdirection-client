namespace Misdirection.Client;

/// <summary>
/// Constants from PROTOCOL.md that are shared by the codec, the parser and the client.
/// </summary>
public static class Protocol
{
    /// <summary>Start-of-frame marker.</summary>
    public const byte StartOfFrame = 0xAB;

    /// <summary>Protocol version this client speaks; the firmware reports its own in PONG.</summary>
    public const byte Version = 1;

    /// <summary>
    /// Largest payload the firmware accepts. Anything longer is NACK(3)'d, so the host-side
    /// parser applies the same limit to keep a corrupt length byte from swallowing the stream.
    /// </summary>
    public const int MaxPayloadLength = 16;

    /// <summary>Frame overhead: SOF + type + len + sum.</summary>
    public const int FrameOverhead = 4;

    /// <summary>Default serial baud rate (115200 8N1, no flow control).</summary>
    public const int DefaultBaudRate = 115200;

    /// <summary>Screen size the firmware assumes until it receives SCREEN_SIZE.</summary>
    public static readonly (ushort Width, ushort Height) DefaultScreenSize = (1920, 1080);

    /// <summary>Smallest screen dimension the firmware core will accept per axis.</summary>
    public const ushort MinScreenDimension = 128;

    /// <summary>Largest screen dimension the firmware core will accept per axis.</summary>
    public const ushort MaxScreenDimension = 7680;

    /// <summary>
    /// True for record types that exist only inside <c>.msdr</c> files and are never valid on the wire
    /// (currently just <see cref="MessageType.FileDelay"/>). The type-range test in
    /// <see cref="Message.IsHostToDevice"/> cannot tell these apart from real host-to-device messages.
    /// </summary>
    public static bool IsFileOnly(MessageType type) => type == MessageType.FileDelay;
}

/// <summary>
/// Frame type byte. Host-to-device types are &lt; 0x80; device-to-host are &gt;= 0x80. 0x7F is reserved
/// for the file-only <see cref="FileDelay"/> record so the wire and file namespaces cannot collide.
/// </summary>
public enum MessageType : byte
{
    Panic = 0x00,
    KeyDown = 0x01,
    KeyUp = 0x02,
    MouseMove = 0x03,
    MouseButtons = 0x04,
    MouseWheel = 0x05,
    ScreenSize = 0x06,
    Ping = 0x07,
    MouseMoveRel = 0x08,

    /// <summary>Gap before the next frame in a <c>.msdr</c> file. Never sent on the wire.</summary>
    FileDelay = 0x7F,

    Pong = 0x80,
    Nack = 0x81,
}

/// <summary>Reason code carried by a NACK frame.</summary>
public enum NackReason : byte
{
    Unknown = 0,
    BadChecksum = 1,
    UnknownType = 2,
    BadLength = 3,
    Disarmed = 4,
    KeyRolloverFull = 5,
}

/// <summary>Absolute mouse button state. One bit per button; the mask replaces the previous state.</summary>
[Flags]
public enum MouseButtons : byte
{
    None = 0,
    Left = 1 << 0,
    Right = 1 << 1,
    Middle = 1 << 2,
    Back = 1 << 3,
    Forward = 1 << 4,
}
