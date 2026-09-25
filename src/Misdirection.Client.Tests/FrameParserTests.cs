namespace Misdirection.Client.Tests;

public class FrameParserTests
{
    private static readonly byte[] Pong = [0xAB, 0x80, 0x01, 0x01, 0x82];
    private static readonly byte[] Nack = [0xAB, 0x81, 0x01, 0x01, 0x83];

    [Fact]
    public void ParsesOneByteAtATime()
    {
        var parser = new FrameParser();
        Frame? got = null;
        for (var i = 0; i < Pong.Length; i++)
        {
            var done = parser.Push(Pong[i], out got);
            Assert.Equal(i == Pong.Length - 1, done);
        }
        Assert.Equal(new PongMessage(1), Message.Decode(got!));
    }

    [Fact]
    public void ParsesAcrossArbitraryChunkBoundaries()
    {
        var stream = Pong.Concat(Nack).Concat(Pong).ToArray();
        for (var chunk = 1; chunk <= stream.Length; chunk++)
        {
            var parser = new FrameParser();
            var frames = new List<Frame>();
            foreach (var piece in stream.Chunk(chunk))
                parser.Feed(piece, frames.Add);
            Assert.Equal(3, frames.Count);
            Assert.Equal(MessageType.Nack, frames[1].Type);
        }
    }

    [Fact]
    public void HuntsPastGarbageBeforeSof()
    {
        var input = new byte[] { 0x00, 0x01, 0xFF, 0x80 }.Concat(Pong).ToArray();
        var parser = new FrameParser();
        var frames = parser.Feed(input);
        Assert.Single(frames);
        Assert.Equal(0, parser.DiscardedCount);
    }

    [Fact]
    public void BadChecksumDiscardsAndResyncs()
    {
        var corrupt = (byte[])Pong.Clone();
        corrupt[^1] ^= 0x01;
        var parser = new FrameParser();
        var reasons = new List<FrameDiscardReason>();
        parser.FrameDiscarded += reasons.Add;

        var frames = parser.Feed(corrupt.Concat(Nack).ToArray());

        Assert.Single(frames);
        Assert.Equal(MessageType.Nack, frames[0].Type);
        Assert.Equal([FrameDiscardReason.BadChecksum], reasons);
        Assert.Equal(1, parser.DiscardedCount);
    }

    [Fact]
    public void UnknownTypeDiscardsAndResyncs()
    {
        var parser = new FrameParser();
        var reasons = new List<FrameDiscardReason>();
        parser.FrameDiscarded += reasons.Add;

        var frames = parser.Feed(new byte[] { 0xAB, 0x7E, 0x00, 0x7E }.Concat(Pong).ToArray());

        Assert.Single(frames);
        Assert.Equal([FrameDiscardReason.UnknownType], reasons);
    }

    [Fact]
    public void UnknownTypeCanBePassedThrough()
    {
        var parser = new FrameParser { RejectUnknownTypes = false };
        var frames = parser.Feed([0xAB, 0x7E, 0x01, 0x42, 0xC1]);
        var frame = Assert.Single(frames);
        Assert.Equal((MessageType)0x7E, frame.Type);
        Assert.Equal([0x42], frame.Payload.ToArray());
    }

    [Fact]
    public void LengthOverSixteenDiscardsWithoutSwallowingStream()
    {
        // A corrupt len of 0xFF must not eat the next 255 bytes.
        var parser = new FrameParser();
        var reasons = new List<FrameDiscardReason>();
        parser.FrameDiscarded += reasons.Add;

        var frames = parser.Feed(new byte[] { 0xAB, 0x07, 0xFF }.Concat(Pong).ToArray());

        Assert.Single(frames);
        Assert.Equal([FrameDiscardReason.BadLength], reasons);
    }

    [Fact]
    public void SofInsidePayloadIsNotAFramingHazard()
    {
        // MOUSE_MOVE to (0xAB, 0xAB): payload contains two 0xAB bytes.
        var msg = new MouseMoveMessage(0xAB, 0xAB);
        var frames = FrameParser.Parse(msg.ToBytes().Concat(Pong).ToArray());
        Assert.Equal(2, frames.Count);
        Assert.Equal(msg, Message.Decode(frames[0]));
    }

    [Fact]
    public void ResetDropsPartialFrame()
    {
        var parser = new FrameParser();
        parser.Feed(Pong.AsSpan(0, 3));
        parser.Reset();
        var frames = parser.Feed(Pong.AsSpan(3));
        Assert.Empty(frames);
        Assert.Single(parser.Feed(Pong));
    }

    [Fact]
    public void ZeroLengthFrameGoesStraightToChecksum()
    {
        var frames = FrameParser.Parse([0xAB, 0x07, 0x00, 0x07]);
        Assert.Equal(new PingMessage(), Message.Decode(frames.Single()));
    }
}
