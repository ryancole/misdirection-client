namespace Misdirection.Client.Tests;

public class VectorTests
{
    [Fact]
    public void VectorFileMatchesProtocolConstants()
    {
        Assert.Equal(Protocol.Version, ProtocolVectors.All.ProtocolVersion);
        Assert.Equal(Protocol.StartOfFrame, ProtocolVectors.All.Sof);
        Assert.NotEmpty(ProtocolVectors.All.Vectors);
    }

    [Theory]
    [MemberData(nameof(ProtocolVectors.AsTheoryData), MemberType = typeof(ProtocolVectors))]
    public void EncodesToSpecBytes(ProtocolVectors.Vector v)
    {
        var message = ProtocolVectors.ToMessage(v);
        Assert.Equal(v.Type, (byte)message.Type);
        Assert.Equal(v.Encoded, message.ToBytes());
        Assert.Equal(v.Hex, Convert.ToHexString(message.ToBytes()).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + " " + b));
    }

    [Theory]
    [MemberData(nameof(ProtocolVectors.AsTheoryData), MemberType = typeof(ProtocolVectors))]
    public void DecodesFromSpecBytes(ProtocolVectors.Vector v)
    {
        var frames = FrameParser.Parse(v.Encoded);
        var frame = Assert.Single(frames);
        Assert.Equal(ProtocolVectors.ToMessage(v), Message.Decode(frame));
    }

    [Theory]
    [MemberData(nameof(ProtocolVectors.AsTheoryData), MemberType = typeof(ProtocolVectors))]
    public void DirectionMatchesTypeRange(ProtocolVectors.Vector v)
    {
        var message = ProtocolVectors.ToMessage(v);
        // IsHostToDevice is purely the type range, so the file-only 0x7F lands on the host side of it.
        Assert.Equal(v.Direction != "device_to_host", message.IsHostToDevice);
        Assert.Equal(v.Direction == "file_only", message.IsFileOnly);
        Assert.Equal(v.Wire, !message.IsFileOnly);
    }

    [Theory]
    [MemberData(nameof(ProtocolVectors.FileOnlyTheoryData), MemberType = typeof(ProtocolVectors))]
    public void FileOnlyVectorsAreFileDelays(ProtocolVectors.Vector v)
    {
        Assert.Equal("FILE_DELAY", v.TypeName);
        Assert.Equal(0x7F, v.Type);
        Assert.False(v.Wire);

        var delay = Assert.IsType<DelayMessage>(ProtocolVectors.ToMessage(v));
        Assert.Equal(MessageType.FileDelay, delay.Type);
        Assert.Equal(v.Fields["micros"], delay.Microseconds);
        Assert.Equal(TimeSpan.FromTicks(v.Fields["micros"] * TimeSpan.TicksPerMicrosecond), delay.Duration);
    }

    [Fact]
    public void VectorFileCoversTheFileDelayRange()
    {
        var micros = ProtocolVectors.All.Vectors.Where(v => v.TypeName == "FILE_DELAY").Select(v => v.Fields["micros"]).ToArray();
        Assert.Contains(0L, micros);
        Assert.Contains((long)uint.MaxValue, micros);
        Assert.All(ProtocolVectors.All.Vectors, v => Assert.Equal(v.TypeName == "FILE_DELAY", !v.Wire));
    }

    [Theory]
    [MemberData(nameof(ProtocolVectors.FileOnlyTheoryData), MemberType = typeof(ProtocolVectors))]
    public void FileOnlyVectorsRoundTripThroughAProtocolFile(ProtocolVectors.Vector v)
    {
        // The file body is the frame encoding, so a delay record is exactly the vector bytes.
        var message = ProtocolVectors.ToMessage(v);
        using var ms = new MemoryStream();
        ProtocolFile.Write(ms, [message], leaveOpen: true);
        Assert.Equal(v.Bytes.Select(b => (byte)b), ms.ToArray().Skip(ProtocolFile.HeaderLength));
        ms.Position = 0;
        Assert.Equal([message], ProtocolFile.Read(ms));
    }

    [Fact]
    public void ShiftDragSequenceEncodesBackToBack()
    {
        // The spec's worked example: KEY_DOWN 0xE1, MOUSE_BTN 0x01, moves, MOUSE_BTN 0x00, KEY_UP 0xE1.
        Message[] sequence =
        [
            new KeyDownMessage(HidUsage.LeftShift),
            new MouseButtonsMessage(MouseButtons.Left),
            new MouseMoveMessage(0, 0),
            new MouseMoveMessage(960, 540),
            new MouseButtonsMessage(MouseButtons.None),
            new KeyUpMessage(HidUsage.LeftShift),
        ];

        var wire = sequence.SelectMany(m => m.ToBytes()).ToArray();
        var decoded = FrameParser.Parse(wire).Select(Message.Decode).ToArray();
        Assert.Equal(sequence, decoded);
    }
}
