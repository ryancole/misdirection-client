namespace Misdirection.Client;

/// <summary>
/// On-disk container for a sequence of protocol messages. The body is the wire encoding itself,
/// frames back to back with no separators, so a file round-trips every message exactly and can be
/// replayed to the device by copying the bytes after the header. A fixed header identifies the
/// format and records the protocol version the frames were written with:
/// <code>
/// offset 0..3  magic "MSDR"
/// offset 4     file format version (see <see cref="FormatVersion"/>)
/// offset 5     protocol version (see <see cref="Protocol.Version"/>)
/// offset 6..   frames, each [0xAB][type][len][payload][sum]
/// </code>
/// The static helpers cover the read-everything / write-everything case; use
/// <see cref="ProtocolFileWriter"/> and <see cref="ProtocolFileReader"/> to stream.
/// </summary>
public static class ProtocolFile
{
    /// <summary>Magic bytes at offset 0.</summary>
    public static ReadOnlySpan<byte> Magic => "MSDR"u8;

    /// <summary>Layout version of the file container, independent of the protocol version.</summary>
    public const byte FormatVersion = 1;

    /// <summary>Header size in bytes; frames start at this offset.</summary>
    public const int HeaderLength = 6;

    /// <summary>Writes the header for the current format and protocol version.</summary>
    public static void WriteHeader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[HeaderLength];
        Magic.CopyTo(header);
        header[4] = FormatVersion;
        header[5] = Protocol.Version;
        stream.Write(header);
    }

    /// <summary>
    /// Reads and validates a header. Throws <see cref="ProtocolFileException"/> if the magic, format
    /// version or protocol version is not one this library understands.
    /// </summary>
    /// <returns>The protocol version recorded in the header.</returns>
    public static byte ReadHeader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[HeaderLength];
        var read = stream.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false);
        if (read < HeaderLength)
            throw new ProtocolFileException($"File is {read} bytes; a protocol file has at least a {HeaderLength}-byte header.");
        if (!header[..4].SequenceEqual(Magic))
            throw new ProtocolFileException($"Not a protocol file: expected magic {Convert.ToHexString(Magic)}, found {Convert.ToHexString(header[..4])}.");
        if (header[4] != FormatVersion)
            throw new ProtocolFileException($"Unsupported file format version {header[4]}; this library reads version {FormatVersion}.");
        if (header[5] != Protocol.Version)
            throw new ProtocolFileException($"File was written for protocol version {header[5]}; this library speaks version {Protocol.Version}.");
        return header[5];
    }

    /// <summary>Writes <paramref name="messages"/> to a new file, replacing any existing file at <paramref name="path"/>.</summary>
    public static void Write(string path, IEnumerable<Message> messages)
    {
        using var writer = ProtocolFileWriter.Create(path);
        writer.Write(messages);
    }

    /// <summary>Writes a header and <paramref name="messages"/> to <paramref name="stream"/> at its current position.</summary>
    public static void Write(Stream stream, IEnumerable<Message> messages, bool leaveOpen = true)
    {
        using var writer = new ProtocolFileWriter(stream, leaveOpen);
        writer.Write(messages);
    }

    /// <summary>Reads every message in the file. Throws <see cref="ProtocolFileException"/> on any malformed content.</summary>
    public static IReadOnlyList<Message> Read(string path)
    {
        using var reader = ProtocolFileReader.Open(path);
        return reader.ReadToEnd();
    }

    /// <summary>Reads every message from <paramref name="stream"/>, starting with the header at its current position.</summary>
    public static IReadOnlyList<Message> Read(Stream stream, bool leaveOpen = true)
    {
        using var reader = new ProtocolFileReader(stream, leaveOpen);
        return reader.ReadToEnd();
    }

    /// <summary>Reads every frame in the file without decoding it to a typed message.</summary>
    public static IReadOnlyList<Frame> ReadFrames(string path)
    {
        using var reader = ProtocolFileReader.Open(path);
        return reader.ReadFramesToEnd();
    }
}

/// <summary>
/// Appends frames to a protocol file as they are produced, e.g. to record a session. The header is
/// written on construction, or validated when appending to an existing file.
/// </summary>
public sealed class ProtocolFileWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _buffer = new byte[Protocol.FrameOverhead + Protocol.MaxPayloadLength];
    private bool _disposed;

    /// <summary>Writes a header to <paramref name="stream"/> at its current position and prepares to append frames.</summary>
    public ProtocolFileWriter(Stream stream, bool leaveOpen = false)
        : this(stream, leaveOpen, writeHeader: true) { }

    private ProtocolFileWriter(Stream stream, bool leaveOpen, bool writeHeader)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(stream));
        _stream = stream;
        _leaveOpen = leaveOpen;
        if (!writeHeader) return;
        try
        {
            ProtocolFile.WriteHeader(stream);
        }
        catch
        {
            if (!leaveOpen) stream.Dispose();
            throw;
        }
    }

    /// <summary>Creates or truncates the file at <paramref name="path"/> and writes the header.</summary>
    public static ProtocolFileWriter Create(string path) =>
        new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), leaveOpen: false, writeHeader: true);

    /// <summary>
    /// Opens <paramref name="path"/> for appending. An existing file must carry a valid header, which is
    /// checked before any frame is added; a missing or empty file gets a fresh header.
    /// </summary>
    public static ProtocolFileWriter Append(string path)
    {
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            if (stream.Length == 0)
            {
                ProtocolFile.WriteHeader(stream);
            }
            else
            {
                ProtocolFile.ReadHeader(stream);
                stream.Seek(0, SeekOrigin.End);
            }
            return new ProtocolFileWriter(stream, leaveOpen: false, writeHeader: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Number of frames written through this instance.</summary>
    public long FramesWritten { get; private set; }

    /// <summary>Appends one frame.</summary>
    public void Write(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var n = frame.Encode(_buffer);
        _stream.Write(_buffer, 0, n);
        FramesWritten++;
    }

    /// <summary>Appends one message.</summary>
    public void Write(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Write(message.ToFrame());
    }

    /// <summary>Appends each message in order.</summary>
    public void Write(IEnumerable<Message> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        foreach (var m in messages) Write(m);
    }

    /// <summary>Flushes the underlying stream.</summary>
    public void Flush() => _stream.Flush();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream.Flush(); }
        finally { if (!_leaveOpen) _stream.Dispose(); }
    }
}

/// <summary>
/// Reads a protocol file one frame at a time. Unlike the serial-port parser, which resyncs past
/// noise, the reader is strict: any byte that is not part of a well-formed frame, and any frame cut
/// short by the end of the file, is reported as a <see cref="ProtocolFileException"/> with its offset.
/// </summary>
public sealed class ProtocolFileReader : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly FrameParser _parser = new();
    private readonly byte[] _buffer = new byte[4096];
    private int _bufferLength;
    private int _bufferPos;
    private long _offset;
    private FrameDiscardReason? _discard;
    private bool _disposed;

    /// <summary>Reads and validates the header from <paramref name="stream"/> at its current position.</summary>
    public ProtocolFileReader(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        _stream = stream;
        _leaveOpen = leaveOpen;
        try
        {
            ProtocolVersion = ProtocolFile.ReadHeader(stream);
        }
        catch
        {
            if (!leaveOpen) stream.Dispose();
            throw;
        }
        _offset = ProtocolFile.HeaderLength;
        _parser.FrameDiscarded += r => _discard = r;
    }

    /// <summary>Opens the file at <paramref name="path"/> and validates its header.</summary>
    public static ProtocolFileReader Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));

    /// <summary>Protocol version recorded in the header.</summary>
    public byte ProtocolVersion { get; }

    /// <summary>Number of frames returned so far.</summary>
    public long FramesRead { get; private set; }

    /// <summary>Reads the next frame; returns false at a clean end of file.</summary>
    public bool TryReadFrame(out Frame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame = null!;
        var frameStart = _offset;

        while (true)
        {
            if (_bufferPos == _bufferLength)
            {
                _bufferLength = _stream.Read(_buffer, 0, _buffer.Length);
                _bufferPos = 0;
                if (_bufferLength == 0)
                {
                    if (!_parser.IsIdle)
                    {
                        _parser.Reset();
                        throw new ProtocolFileException($"File ends inside the frame that starts at offset {frameStart}.");
                    }
                    return false;
                }
            }

            var b = _buffer[_bufferPos++];
            if (_parser.IsIdle)
            {
                if (b != Protocol.StartOfFrame)
                    throw new ProtocolFileException($"Expected start of frame (0x{Protocol.StartOfFrame:X2}) at offset {_offset}, found 0x{b:X2}.");
                frameStart = _offset;
            }
            _offset++;

            var complete = _parser.Push(b, out var parsed);
            if (_discard is { } reason)
            {
                _discard = null;
                throw new ProtocolFileException($"{Describe(reason)} in the frame that starts at offset {frameStart}.");
            }
            if (complete)
            {
                FramesRead++;
                frame = parsed!;
                return true;
            }
        }
    }

    /// <summary>Reads and decodes the next message; returns false at a clean end of file.</summary>
    public bool TryRead(out Message message)
    {
        message = null!;
        var frameStart = _offset;
        if (!TryReadFrame(out var frame)) return false;
        if (!Message.TryDecode(frame, out message, out var error))
            throw new ProtocolFileException($"{error} (frame at offset {frameStart})");
        return true;
    }

    /// <summary>Reads the remaining messages in order.</summary>
    public IReadOnlyList<Message> ReadToEnd()
    {
        var messages = new List<Message>();
        while (TryRead(out var m)) messages.Add(m);
        return messages;
    }

    /// <summary>Reads the remaining frames in order, without decoding them.</summary>
    public IReadOnlyList<Frame> ReadFramesToEnd()
    {
        var frames = new List<Frame>();
        while (TryReadFrame(out var f)) frames.Add(f);
        return frames;
    }

    /// <summary>Enumerates the remaining messages lazily.</summary>
    public IEnumerable<Message> ReadAll()
    {
        while (TryRead(out var m)) yield return m;
    }

    private static string Describe(FrameDiscardReason reason) => reason switch
    {
        FrameDiscardReason.BadChecksum => "Bad checksum",
        FrameDiscardReason.UnknownType => "Unknown message type",
        FrameDiscardReason.BadLength => $"Payload length over {Protocol.MaxPayloadLength}",
        _ => reason.ToString(),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_leaveOpen) _stream.Dispose();
    }
}

/// <summary>Thrown when a file is not a protocol file or its contents are malformed.</summary>
public sealed class ProtocolFileException(string message) : Exception(message);
