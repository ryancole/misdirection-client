namespace Misdirection.Client;

/// <summary>Why the parser threw a frame away and went back to HUNT.</summary>
public enum FrameDiscardReason
{
    BadChecksum,
    UnknownType,
    BadLength,
}

/// <summary>
/// Incremental frame reader: HUNT -> TYPE -> LEN -> PAYLOAD -> SUM. Feed it bytes as they
/// arrive from the serial port in any chunking; it emits a <see cref="Frame"/> per complete,
/// well-formed frame and resyncs on anything else.
/// </summary>
public sealed class FrameParser
{
    private enum State { Hunt, Type, Len, Payload, Sum }

    private readonly byte[] _payload = new byte[Protocol.MaxPayloadLength];
    private State _state = State.Hunt;
    private byte _type;
    private int _len;
    private int _received;

    /// <summary>Raised when a frame is discarded. The parser has already resynced when this fires.</summary>
    public event Action<FrameDiscardReason>? FrameDiscarded;

    /// <summary>Running count of discarded frames, for diagnostics.</summary>
    public long DiscardedCount { get; private set; }

    /// <summary>True when no frame is in progress, i.e. the next byte must be a start-of-frame marker.</summary>
    public bool IsIdle => _state == State.Hunt;

    /// <summary>
    /// When true (the default), a type byte that is not a defined <see cref="MessageType"/> discards the
    /// frame, as the spec requires. Set false to pass unknown types through, e.g. for protocol tooling.
    /// </summary>
    public bool RejectUnknownTypes { get; init; } = true;

    /// <summary>Feeds one byte. Returns true and sets <paramref name="frame"/> when a frame completes.</summary>
    public bool Push(byte b, out Frame? frame)
    {
        frame = null;
        switch (_state)
        {
            case State.Hunt:
                if (b == Protocol.StartOfFrame) _state = State.Type;
                return false;

            case State.Type:
                if (RejectUnknownTypes && !Enum.IsDefined((MessageType)b))
                {
                    Discard(FrameDiscardReason.UnknownType);
                    return false;
                }
                _type = b;
                _state = State.Len;
                return false;

            case State.Len:
                if (b > Protocol.MaxPayloadLength)
                {
                    Discard(FrameDiscardReason.BadLength);
                    return false;
                }
                _len = b;
                _received = 0;
                _state = _len == 0 ? State.Sum : State.Payload;
                return false;

            case State.Payload:
                _payload[_received++] = b;
                if (_received == _len) _state = State.Sum;
                return false;

            case State.Sum:
                var payload = _payload.AsSpan(0, _len);
                _state = State.Hunt;
                if (b != FrameCodec.Checksum((MessageType)_type, payload))
                {
                    Discard(FrameDiscardReason.BadChecksum);
                    return false;
                }
                frame = new Frame((MessageType)_type, payload);
                return true;

            default:
                throw new InvalidOperationException($"Unexpected parser state {_state}.");
        }
    }

    /// <summary>Feeds a chunk of bytes, invoking <paramref name="onFrame"/> for each completed frame.</summary>
    public void Feed(ReadOnlySpan<byte> bytes, Action<Frame> onFrame)
    {
        foreach (var b in bytes)
        {
            if (Push(b, out var frame)) onFrame(frame!);
        }
    }

    /// <summary>Feeds a chunk of bytes and collects every completed frame.</summary>
    public List<Frame> Feed(ReadOnlySpan<byte> bytes)
    {
        var frames = new List<Frame>();
        Feed(bytes, frames.Add);
        return frames;
    }

    /// <summary>Drops any partial frame and returns to HUNT.</summary>
    public void Reset() => _state = State.Hunt;

    /// <summary>One-shot parse of a complete buffer with a fresh parser.</summary>
    public static List<Frame> Parse(ReadOnlySpan<byte> bytes) => new FrameParser().Feed(bytes);

    private void Discard(FrameDiscardReason reason)
    {
        _state = State.Hunt;
        DiscardedCount++;
        FrameDiscarded?.Invoke(reason);
    }
}
