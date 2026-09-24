namespace Misdirection.Client;

/// <summary>
/// One wire frame: <c>[0xAB][type][len][payload][sum]</c>. Immutable; the payload is copied on construction.
/// </summary>
public sealed class Frame : IEquatable<Frame>
{
    private readonly byte[] _payload;

    public Frame(MessageType type, ReadOnlySpan<byte> payload = default)
    {
        if (payload.Length > Protocol.MaxPayloadLength)
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"Payload is {payload.Length} bytes; the protocol allows at most {Protocol.MaxPayloadLength}.");

        Type = type;
        _payload = payload.ToArray();
    }

    public MessageType Type { get; }

    public ReadOnlySpan<byte> Payload => _payload;

    /// <summary>Total encoded size, including SOF, header and checksum.</summary>
    public int EncodedLength => FrameCodec.EncodedLength(_payload.Length);

    /// <summary>Checksum as it appears on the wire: <c>(type + len + payload) &amp; 0xFF</c>.</summary>
    public byte Checksum => FrameCodec.Checksum(Type, _payload);

    /// <summary>Encodes the frame into a fresh array.</summary>
    public byte[] Encode() => FrameCodec.Encode(Type, _payload);

    /// <summary>Encodes the frame into <paramref name="destination"/>; returns the number of bytes written.</summary>
    public int Encode(Span<byte> destination) => FrameCodec.Encode(Type, _payload, destination);

    public bool Equals(Frame? other) =>
        other is not null && Type == other.Type && Payload.SequenceEqual(other.Payload);

    public override bool Equals(object? obj) => Equals(obj as Frame);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Type);
        hash.AddBytes(_payload);
        return hash.ToHashCode();
    }

    public override string ToString() => $"{Type} [{Convert.ToHexString(_payload)}]";
}

/// <summary>Stateless encode helpers for the framing layer.</summary>
public static class FrameCodec
{
    /// <summary>Encoded size of a frame carrying <paramref name="payloadLength"/> payload bytes.</summary>
    public static int EncodedLength(int payloadLength) => Protocol.FrameOverhead + payloadLength;

    /// <summary><c>(type + len + payload bytes) &amp; 0xFF</c>.</summary>
    public static byte Checksum(MessageType type, ReadOnlySpan<byte> payload)
    {
        int sum = (byte)type + payload.Length;
        foreach (var b in payload) sum += b;
        return (byte)sum;
    }

    /// <summary>Encodes a frame into a fresh array.</summary>
    public static byte[] Encode(MessageType type, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[EncodedLength(payload.Length)];
        Encode(type, payload, buffer);
        return buffer;
    }

    /// <summary>Encodes a frame into <paramref name="destination"/>; returns the number of bytes written.</summary>
    public static int Encode(MessageType type, ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        if (payload.Length > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(payload), "Payload length must fit in one byte.");

        var needed = EncodedLength(payload.Length);
        if (destination.Length < needed)
            throw new ArgumentException($"Destination needs {needed} bytes, has {destination.Length}.", nameof(destination));

        destination[0] = Protocol.StartOfFrame;
        destination[1] = (byte)type;
        destination[2] = (byte)payload.Length;
        payload.CopyTo(destination[3..]);
        destination[3 + payload.Length] = Checksum(type, payload);
        return needed;
    }
}
