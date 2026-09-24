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
        Assert.Equal(v.Wire, message.ToBytes());
        Assert.Equal(v.Hex, Convert.ToHexString(message.ToBytes()).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + " " + b));
    }

    [Theory]
    [MemberData(nameof(ProtocolVectors.AsTheoryData), MemberType = typeof(ProtocolVectors))]
    public void DecodesFromSpecBytes(ProtocolVectors.Vector v)
    {
        var frames = FrameParser.Parse(v.Wire);
        var frame = Assert.Single(frames);
        Assert.Equal(ProtocolVectors.ToMessage(v), Message.Decode(frame));
    }

    [Theory]
    [MemberData(nameof(ProtocolVectors.AsTheoryData), MemberType = typeof(ProtocolVectors))]
    public void DirectionMatchesTypeRange(ProtocolVectors.Vector v)
    {
        var message = ProtocolVectors.ToMessage(v);
        Assert.Equal(v.Direction == "host_to_device", message.IsHostToDevice);
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
