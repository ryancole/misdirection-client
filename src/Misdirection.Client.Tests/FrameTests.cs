namespace Misdirection.Client.Tests;

public class FrameTests
{
    [Fact]
    public void ChecksumIsTypePlusLenPlusPayloadMod256()
    {
        // MOUSE_MOVE (960,540): 03 + 04 + C0 + 03 + 1C + 02 = 0xE8
        Assert.Equal(0xE8, FrameCodec.Checksum(MessageType.MouseMove, [0xC0, 0x03, 0x1C, 0x02]));
        // Wraps: 0x81 + 1 + 0xFF = 0x181 -> 0x81
        Assert.Equal(0x81, FrameCodec.Checksum(MessageType.Nack, [0xFF]));
    }

    [Fact]
    public void EncodeIntoSpanReportsLengthAndRejectsShortBuffers()
    {
        var frame = new Frame(MessageType.KeyDown, [HidUsage.A]);
        Span<byte> buf = stackalloc byte[8];
        Assert.Equal(5, frame.Encode(buf));
        Assert.Equal(new byte[] { 0xAB, 0x01, 0x01, 0x04, 0x06 }, buf[..5].ToArray());

        var tooSmall = new byte[4];
        Assert.Throws<ArgumentException>(() => frame.Encode(tooSmall));
    }

    [Fact]
    public void PayloadOverSixteenBytesIsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Frame(MessageType.Ping, new byte[17]));
        _ = new Frame(MessageType.Ping, new byte[16]);
    }

    [Fact]
    public void FrameEqualityIsStructural()
    {
        var a = new Frame(MessageType.MouseWheel, [0xFF, 0x00]);
        var b = new Frame(MessageType.MouseWheel, [0xFF, 0x00]);
        var c = new Frame(MessageType.MouseWheel, [0x01, 0x00]);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void PayloadIsCopiedNotAliased()
    {
        var bytes = new byte[] { 0x04 };
        var frame = new Frame(MessageType.KeyDown, bytes);
        bytes[0] = 0x05;
        Assert.Equal(0x04, frame.Payload[0]);
    }

    [Fact]
    public void DecodeRejectsWrongPayloadLength()
    {
        var bad = new Frame(MessageType.MouseMove, [0x00, 0x00]);
        Assert.False(Message.TryDecode(bad, out _, out var error));
        Assert.Contains("4-byte", error);
        Assert.Throws<ProtocolException>(() => Message.Decode(bad));
    }

    [Fact]
    public void WheelUsesTwosComplement()
    {
        var msg = new MouseWheelMessage(-1, -128);
        Assert.Equal(new byte[] { 0xAB, 0x05, 0x02, 0xFF, 0x80, 0x86 }, msg.ToBytes());
        Assert.Equal(msg, Message.Decode(FrameParser.Parse(msg.ToBytes()).Single()));
    }

    [Fact]
    public void ButtonMaskBitsMatchSpec()
    {
        Assert.Equal(0x01, (byte)MouseButtons.Left);
        Assert.Equal(0x02, (byte)MouseButtons.Right);
        Assert.Equal(0x04, (byte)MouseButtons.Middle);
        Assert.Equal(0x08, (byte)MouseButtons.Back);
        Assert.Equal(0x10, (byte)MouseButtons.Forward);
    }

    [Fact]
    public void ModifierRangeIsE0ToE7()
    {
        Assert.True(HidUsage.IsModifier(HidUsage.LeftControl));
        Assert.True(HidUsage.IsModifier(HidUsage.RightGui));
        Assert.False(HidUsage.IsModifier(HidUsage.A));
        Assert.False(HidUsage.IsModifier(0xE8));
    }
}
