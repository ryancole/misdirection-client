namespace Misdirection.Client.Tests;

public class ProtocolFileTests : IDisposable
{
    private static readonly byte[] Header = [(byte)'M', (byte)'S', (byte)'D', (byte)'R', ProtocolFile.FormatVersion, Protocol.Version];

    private static readonly Message[] ShiftDrag =
    [
        new ScreenSizeMessage(1920, 1080),
        new KeyDownMessage(HidUsage.LeftShift),
        new MouseMoveMessage(100, 100),
        new MouseButtonsMessage(MouseButtons.Left),
        new MouseMoveMessage(500, 400),
        new MouseWheelMessage(-1, 0),
        new MouseButtonsMessage(MouseButtons.None),
        new KeyUpMessage(HidUsage.LeftShift),
        new PingMessage(),
        new PongMessage(1),
        new NackMessage(NackReason.Disarmed),
        new PanicMessage(),
    ];

    private readonly string _dir = Directory.CreateTempSubdirectory("misdirection-protocolfile-").FullName;

    private string TempPath(string name = "messages.msdr") => Path.Combine(_dir, name);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void HeaderIsMagicThenFormatVersionThenProtocolVersion()
    {
        Assert.Equal("MSDR", System.Text.Encoding.ASCII.GetString(ProtocolFile.Magic));
        Assert.Equal(ProtocolFile.HeaderLength, Header.Length);

        using var ms = new MemoryStream();
        ProtocolFile.Write(ms, []);
        Assert.Equal(Header, ms.ToArray());
    }

    [Fact]
    public void BodyIsTheWireEncodingWithNoSeparators()
    {
        using var ms = new MemoryStream();
        ProtocolFile.Write(ms, ShiftDrag);

        var expected = Header.Concat(ShiftDrag.SelectMany(m => m.ToBytes())).ToArray();
        Assert.Equal(expected, ms.ToArray());
    }

    [Fact]
    public void RoundTripsThroughAFile()
    {
        var path = TempPath();
        ProtocolFile.Write(path, ShiftDrag);
        Assert.Equal(ShiftDrag, ProtocolFile.Read(path));
        Assert.Equal(ShiftDrag.Select(m => m.ToFrame()), ProtocolFile.ReadFrames(path));
    }

    [Fact]
    public void RoundTripsEverySpecVector()
    {
        var messages = ProtocolVectors.All.Vectors.Select(ProtocolVectors.ToMessage).ToArray();
        var path = TempPath();
        ProtocolFile.Write(path, messages);
        Assert.Equal(messages, ProtocolFile.Read(path));
    }

    [Fact]
    public void WriteReplacesAnExistingFile()
    {
        var path = TempPath();
        ProtocolFile.Write(path, ShiftDrag);
        ProtocolFile.Write(path, [new PingMessage()]);
        Assert.Equal([new PingMessage()], ProtocolFile.Read(path));
    }

    [Fact]
    public void EmptyFileReadsAsNoMessages()
    {
        var path = TempPath();
        ProtocolFile.Write(path, []);
        Assert.Equal(Header.Length, new FileInfo(path).Length);
        Assert.Empty(ProtocolFile.Read(path));
    }

    [Fact]
    public void AppendCreatesThenExtendsAFile()
    {
        var path = TempPath();
        using (var w = ProtocolFileWriter.Append(path))
        {
            w.Write(ShiftDrag[0]);
            w.Write(ShiftDrag[1]);
            Assert.Equal(2, w.FramesWritten);
        }
        using (var w = ProtocolFileWriter.Append(path))
        {
            w.Write(ShiftDrag[2..]);
        }
        Assert.Equal(ShiftDrag, ProtocolFile.Read(path));
        Assert.Equal(Header.Length + ShiftDrag.Sum(m => m.ToFrame().EncodedLength), new FileInfo(path).Length);
    }

    [Fact]
    public void AppendRefusesAFileThatIsNotAProtocolFile()
    {
        var path = TempPath("notes.txt");
        File.WriteAllText(path, "hello there");
        Assert.Throws<ProtocolFileException>(() => ProtocolFileWriter.Append(path));
        Assert.Equal("hello there", File.ReadAllText(path));
    }

    [Fact]
    public void WriterAndReaderStreamOneFrameAtATime()
    {
        using var ms = new MemoryStream();
        using (var w = new ProtocolFileWriter(ms, leaveOpen: true))
        {
            foreach (var m in ShiftDrag) w.Write(m);
        }

        ms.Position = 0;
        using var r = new ProtocolFileReader(ms, leaveOpen: true);
        Assert.Equal(Protocol.Version, r.ProtocolVersion);
        var got = new List<Message>();
        while (r.TryRead(out var m)) got.Add(m);
        Assert.Equal(ShiftDrag, got);
        Assert.Equal(ShiftDrag.Length, r.FramesRead);
        Assert.False(r.TryRead(out _));
        Assert.False(r.TryReadFrame(out _));
    }

    [Fact]
    public void ReadAllIsLazy()
    {
        using var ms = new MemoryStream();
        ProtocolFile.Write(ms, ShiftDrag);
        ms.Position = 0;
        using var r = new ProtocolFileReader(ms, leaveOpen: true);
        Assert.Equal(ShiftDrag[0], r.ReadAll().First());
        Assert.Equal(1, r.FramesRead);
    }

    [Fact]
    public void ReaderSpansBufferBoundaries()
    {
        // Well over the reader's 4 KiB internal buffer, with 4- to 8-byte frames straddling every refill.
        var many = Enumerable.Range(0, 5000).Select(i => (Message)(i % 2 == 0
            ? new MouseMoveMessage((ushort)i, (ushort)(i * 3))
            : new KeyDownMessage((byte)i))).ToArray();
        var path = TempPath();
        ProtocolFile.Write(path, many);
        Assert.Equal(many, ProtocolFile.Read(path));
    }

    [Fact]
    public void LeaveOpenIsHonoured()
    {
        var ms = new MemoryStream();
        ProtocolFile.Write(ms, [new PingMessage()], leaveOpen: true);
        ms.Position = 0;
        Assert.Equal([new PingMessage()], ProtocolFile.Read(ms, leaveOpen: true));
        Assert.True(ms.CanRead);

        ProtocolFile.Write(ms, [], leaveOpen: false);
        Assert.False(ms.CanRead);
    }

    [Theory]
    [InlineData(new byte[0], "at least")]
    [InlineData(new byte[] { (byte)'M', (byte)'S', (byte)'D' }, "at least")]
    [InlineData(new byte[] { (byte)'P', (byte)'K', 3, 4, 1, 1 }, "Not a protocol file")]
    [InlineData(new byte[] { (byte)'M', (byte)'S', (byte)'D', (byte)'R', 2, 1 }, "format version 2")]
    [InlineData(new byte[] { (byte)'M', (byte)'S', (byte)'D', (byte)'R', 1, 9 }, "protocol version 9")]
    public void RejectsBadHeaders(byte[] bytes, string expectedError)
    {
        var ex = Assert.Throws<ProtocolFileException>(() => ProtocolFile.Read(new MemoryStream(bytes)));
        Assert.Contains(expectedError, ex.Message);
    }

    [Fact]
    public void RejectsATruncatedTrailingFrame()
    {
        var bytes = Header.Concat(new PingMessage().ToBytes()).Concat(new byte[] { 0xAB, 0x03, 0x04, 0x00 }).ToArray();
        using var r = new ProtocolFileReader(new MemoryStream(bytes));
        Assert.True(r.TryRead(out var first));
        Assert.Equal(new PingMessage(), first);
        var ex = Assert.Throws<ProtocolFileException>(() => r.TryRead(out _));
        Assert.Contains($"offset {Header.Length + 4}", ex.Message);
    }

    [Fact]
    public void RejectsBytesBetweenFrames()
    {
        var bytes = Header.Concat(new PingMessage().ToBytes()).Concat(new byte[] { 0x00 }).Concat(new PingMessage().ToBytes()).ToArray();
        var ex = Assert.Throws<ProtocolFileException>(() => ProtocolFile.Read(new MemoryStream(bytes)));
        Assert.Contains($"Expected start of frame (0xAB) at offset {Header.Length + 4}, found 0x00", ex.Message);
    }

    [Fact]
    public void RejectsABadChecksumWithTheFrameOffset()
    {
        var frame = new KeyDownMessage(HidUsage.A).ToBytes();
        frame[^1] ^= 0xFF;
        var bytes = Header.Concat(new PingMessage().ToBytes()).Concat(frame).ToArray();
        var ex = Assert.Throws<ProtocolFileException>(() => ProtocolFile.Read(new MemoryStream(bytes)));
        Assert.Contains("Bad checksum", ex.Message);
        Assert.Contains($"offset {Header.Length + 4}", ex.Message);
    }

    [Fact]
    public void RejectsAnUnknownMessageType()
    {
        var bytes = Header.Concat(new byte[] { 0xAB, 0x7F, 0x00, 0x7F }).ToArray();
        var ex = Assert.Throws<ProtocolFileException>(() => ProtocolFile.Read(new MemoryStream(bytes)));
        Assert.Contains("Unknown message type", ex.Message);
    }

    [Fact]
    public void RejectsAKnownTypeWithTheWrongPayloadLength()
    {
        // A well-formed frame at the framing layer that is not a valid message.
        var bytes = Header.Concat(new Frame(MessageType.MouseMove, [1, 2]).Encode()).ToArray();
        var ex = Assert.Throws<ProtocolFileException>(() => ProtocolFile.Read(new MemoryStream(bytes)));
        Assert.Contains("4-byte", ex.Message);
        Assert.Contains($"offset {Header.Length}", ex.Message);

        // ReadFrames does not decode, so it accepts the same file.
        Assert.Single(ProtocolFile.ReadFrames(WriteTemp(bytes)));
    }

    [Fact]
    public void BodyAfterHeaderReplaysThroughTheSerialParser()
    {
        var path = TempPath();
        ProtocolFile.Write(path, ShiftDrag);
        var wire = File.ReadAllBytes(path).AsSpan(ProtocolFile.HeaderLength);
        Assert.Equal(ShiftDrag, FrameParser.Parse(wire).Select(Message.Decode));
    }

    private string WriteTemp(byte[] bytes)
    {
        var path = TempPath("raw.msdr");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
