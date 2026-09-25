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
/// Timing is optional. A <see cref="DelayMessage"/> (type 0x7F, FILE_DELAY) records the gap before the
/// frame that follows it and is never sent on the wire; a file without any replays as fast as the link
/// allows. The static helpers cover the read-everything / write-everything case; use
/// <see cref="ProtocolFileWriter"/> and <see cref="ProtocolFileReader"/> to stream,
/// <see cref="ProtocolFileRecorder"/> to capture a live session with timing, and
/// <see cref="ReadTimed(string)"/> to get a playback schedule.
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

    /// <summary>
    /// Reads every wire message in the file with the time at which a replayer should send it, relative
    /// to the start of the file. Delays are folded into <c>At</c> and not returned. See
    /// <see cref="ProtocolFileReader.ReadTimed"/> for how to schedule against it.
    /// </summary>
    public static IReadOnlyList<(TimeSpan At, Message Message)> ReadTimed(string path)
    {
        using var reader = ProtocolFileReader.Open(path);
        return reader.ReadTimed().ToList();
    }

    /// <summary>Reads every wire message with its playback offset from <paramref name="stream"/>, starting with the header at its current position.</summary>
    public static IReadOnlyList<(TimeSpan At, Message Message)> ReadTimed(Stream stream, bool leaveOpen = true)
    {
        using var reader = new ProtocolFileReader(stream, leaveOpen);
        return reader.ReadTimed().ToList();
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

    /// <summary>
    /// Records a gap before the next frame as one or more <see cref="DelayMessage"/> records.
    /// <paramref name="delay"/> is rounded to the nearest microsecond; zero (after rounding) writes
    /// nothing, and a negative value throws <see cref="ArgumentOutOfRangeException"/>. A gap longer than
    /// <see cref="uint.MaxValue"/> microseconds (about 71.6 minutes) is split across consecutive
    /// records, each carrying the maximum, so any <see cref="TimeSpan"/> can be stored.
    /// </summary>
    /// <returns>The number of records written (zero for a zero delay).</returns>
    public int WriteDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "A file delay cannot be negative.");
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Ticks are 100 ns, so round half up to the nearest 10 ticks.
        var micros = Math.DivRem(delay.Ticks, TimeSpan.TicksPerMicrosecond, out var remainder);
        if (remainder * 2 >= TimeSpan.TicksPerMicrosecond) micros++;

        var records = 0;
        while (micros > 0)
        {
            var chunk = (uint)Math.Min(micros, uint.MaxValue);
            Write(new DelayMessage(chunk));
            micros -= chunk;
            records++;
        }
        return records;
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
/// Captures a live session into a protocol file with timing. Each <see cref="Record"/> call writes a
/// <see cref="DelayMessage"/> for the time elapsed since the previous recorded message (nothing before
/// the first) and then the message itself, so <see cref="ProtocolFileReader.ReadTimed"/> replays the
/// session at its original pace. Time comes from a <see cref="TimeProvider"/> so tests can drive a
/// fake clock; the default is <see cref="TimeProvider.System"/>.
/// </summary>
public sealed class ProtocolFileRecorder : IDisposable
{
    private readonly TimeProvider _time;
    private readonly bool _leaveOpen;
    private long _lastTimestamp;
    private bool _disposed;

    /// <summary>
    /// Wraps <paramref name="writer"/>. Unless <paramref name="leaveOpen"/> is true, disposing the recorder
    /// disposes the writer.
    /// </summary>
    public ProtocolFileRecorder(ProtocolFileWriter writer, TimeProvider? timeProvider = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Writer = writer;
        _time = timeProvider ?? TimeProvider.System;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Creates or truncates the file at <paramref name="path"/> and records into it.</summary>
    public static ProtocolFileRecorder Create(string path, TimeProvider? timeProvider = null) =>
        new(ProtocolFileWriter.Create(path), timeProvider);

    /// <summary>The writer receiving the frames.</summary>
    public ProtocolFileWriter Writer { get; }

    /// <summary>Number of messages recorded through this instance (delays are not counted).</summary>
    public long MessagesRecorded { get; private set; }

    /// <summary>
    /// Writes the gap since the previous recorded message, then <paramref name="message"/>. The gap is
    /// measured when this is called, so record as close to the send or receive as possible. File-only
    /// messages are rejected with <see cref="ArgumentException"/>: the recorder owns the timing.
    /// </summary>
    public void Record(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (message.IsFileOnly)
            throw new ArgumentException($"{message.Type} is a file-only record; the recorder writes delays itself.", nameof(message));

        var now = _time.GetTimestamp();
        if (MessagesRecorded > 0)
            Writer.WriteDelay(_time.GetElapsedTime(_lastTimestamp, now));
        Writer.Write(message);
        _lastTimestamp = now;
        MessagesRecorded++;
    }

    /// <summary>Flushes the underlying writer.</summary>
    public void Flush() => Writer.Flush();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_leaveOpen) Writer.Flush();
        else Writer.Dispose();
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
    private long _elapsedMicros;
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

    /// <summary>
    /// Sum of every <see cref="DelayMessage"/> decoded so far, i.e. the file-time position of the reader.
    /// After reading to the end this includes any delay that trails the last wire message. Frames read
    /// with <see cref="TryReadFrame"/> are not decoded and do not count.
    /// </summary>
    public TimeSpan Elapsed => TimeSpan.FromTicks(_elapsedMicros * TimeSpan.TicksPerMicrosecond);

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
        if (message is DelayMessage delay)
            _elapsedMicros += delay.Microseconds;
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

    /// <summary>Enumerates the remaining messages lazily, delays included.</summary>
    public IEnumerable<Message> ReadAll()
    {
        while (TryRead(out var m)) yield return m;
    }

    /// <summary>
    /// Enumerates the remaining wire messages lazily as a playback schedule. <c>At</c> is the running
    /// total of every delay from the start of the file (see <see cref="Elapsed"/>) up to that message;
    /// the <see cref="DelayMessage"/> records themselves are consumed and not yielded, so everything
    /// returned can be handed to <see cref="MisdirectionClient.SendAsync(Message, CancellationToken)"/>.
    /// <para>
    /// To replay at the recorded pace, take one reference timestamp before the first message and wait
    /// until <c>reference + At</c> for each one, rather than sleeping for each gap in turn. Sleeping per
    /// delay lets timer overshoot accumulate across the file; scheduling against a single clock keeps
    /// every message within one timer error of its recorded time.
    /// </para>
    /// </summary>
    public IEnumerable<(TimeSpan At, Message Message)> ReadTimed()
    {
        while (TryRead(out var m))
        {
            if (m.IsFileOnly) continue;
            yield return (Elapsed, m);
        }
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
