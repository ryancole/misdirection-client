using Microsoft.Extensions.Time.Testing;

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

    private static TimeSpan Micros(long n) => TimeSpan.FromTicks(n * TimeSpan.TicksPerMicrosecond);

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
        // 0x7E is unassigned. (0x7F is FILE_DELAY, which is exactly the error a 0.1.x reader reports
        // for a file with delays, so the message text is part of the compatibility story.)
        var bytes = Header.Concat(new byte[] { 0xAB, 0x7E, 0x00, 0x7E }).ToArray();
        var ex = Assert.Throws<ProtocolFileException>(() => ProtocolFile.Read(new MemoryStream(bytes)));
        Assert.Contains("Unknown message type", ex.Message);
        Assert.Contains($"offset {Header.Length}", ex.Message);
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

    // --- delays ------------------------------------------------------------------------------

    [Fact]
    public void WriteDelayEncodesTheSpecVectors()
    {
        using var ms = new MemoryStream();
        using (var w = new ProtocolFileWriter(ms, leaveOpen: true))
        {
            Assert.Equal(1, w.WriteDelay(Micros(1)));
            Assert.Equal(1, w.WriteDelay(TimeSpan.FromMilliseconds(1)));
            Assert.Equal(1, w.WriteDelay(Micros(16667)));
            Assert.Equal(1, w.WriteDelay(Micros(uint.MaxValue)));
            Assert.Equal(4, w.FramesWritten);
        }
        byte[] expected =
        [
            .. Header,
            0xAB, 0x7F, 0x04, 0x01, 0x00, 0x00, 0x00, 0x84,
            0xAB, 0x7F, 0x04, 0xE8, 0x03, 0x00, 0x00, 0x6E,
            0xAB, 0x7F, 0x04, 0x1B, 0x41, 0x00, 0x00, 0xDF,
            0xAB, 0x7F, 0x04, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F,
        ];
        Assert.Equal(expected, ms.ToArray());
    }

    [Fact]
    public void DelaysRoundTripLikeAnyOtherMessage()
    {
        var path = TempPath();
        using (var w = ProtocolFileWriter.Create(path))
        {
            w.Write(ShiftDrag[0]);
            w.WriteDelay(Micros(16667));
            w.Write(ShiftDrag[1]);
            w.Write(new DelayMessage(0));            // an explicit zero record is legal and preserved
            w.Write(ShiftDrag[2]);
        }
        Message[] expected = [ShiftDrag[0], new DelayMessage(16667), ShiftDrag[1], new DelayMessage(0), ShiftDrag[2]];
        Assert.Equal(expected, ProtocolFile.Read(path));
        Assert.Equal(Micros(16667), ((DelayMessage)ProtocolFile.Read(path)[1]).Duration);
    }

    [Fact]
    public void ZeroDelayWritesNothing()
    {
        using var ms = new MemoryStream();
        using (var w = new ProtocolFileWriter(ms, leaveOpen: true))
        {
            Assert.Equal(0, w.WriteDelay(TimeSpan.Zero));
            Assert.Equal(0, w.WriteDelay(TimeSpan.FromTicks(4)));   // 0.4 us rounds to nothing
            Assert.Equal(0, w.FramesWritten);
        }
        Assert.Equal(Header, ms.ToArray());
    }

    [Fact]
    public void DelayIsRoundedToTheNearestMicrosecond()
    {
        using var ms = new MemoryStream();
        using (var w = new ProtocolFileWriter(ms, leaveOpen: true))
        {
            w.WriteDelay(TimeSpan.FromTicks(14));   // 1.4 us -> 1
            w.WriteDelay(TimeSpan.FromTicks(15));   // 1.5 us -> 2
            w.WriteDelay(TimeSpan.FromTicks(5));    // 0.5 us -> 1
            w.WriteDelay(TimeSpan.FromTicks(10));   // exactly 1 us
        }
        ms.Position = 0;
        Assert.Equal([new DelayMessage(1), new DelayMessage(2), new DelayMessage(1), new DelayMessage(1)], ProtocolFile.Read(ms));
    }

    [Fact]
    public void LongDelaysAreSplitAcrossRecords()
    {
        var max = Micros(uint.MaxValue);
        using var ms = new MemoryStream();
        using (var w = new ProtocolFileWriter(ms, leaveOpen: true))
        {
            Assert.Equal(1, w.WriteDelay(max));
            Assert.Equal(2, w.WriteDelay(max + Micros(1)));
            Assert.Equal(3, w.WriteDelay(TimeSpan.FromHours(3)));   // ~2.5x the maximum
        }
        ms.Position = 0;
        var delays = ProtocolFile.Read(ms).Cast<DelayMessage>().ToArray();

        Assert.Equal(6, delays.Length);
        Assert.Equal(new DelayMessage(uint.MaxValue), delays[0]);
        Assert.Equal([new DelayMessage(uint.MaxValue), new DelayMessage(1)], delays[1..3]);
        Assert.All(delays[3..5], d => Assert.Equal(uint.MaxValue, d.Microseconds));
        Assert.Equal(TimeSpan.FromHours(3), delays[3..].Aggregate(TimeSpan.Zero, (t, d) => t + d.Duration));
    }

    [Fact]
    public void NegativeDelaysAreRejected()
    {
        using var ms = new MemoryStream();
        using var w = new ProtocolFileWriter(ms, leaveOpen: true);
        Assert.Throws<ArgumentOutOfRangeException>(() => w.WriteDelay(TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.WriteDelay(TimeSpan.FromSeconds(-1)));
        Assert.Equal(0, w.FramesWritten);
        Assert.Equal(Header.Length, ms.Length);
    }

    [Fact]
    public void FilesWithDelaysKeepFormatVersionOne()
    {
        var path = TempPath();
        using (var w = ProtocolFileWriter.Create(path))
        {
            w.Write(ShiftDrag[0]);
            w.WriteDelay(Micros(1000));
            w.Write(ShiftDrag[1]);
        }
        // Same header as always; the delay is just another frame in the body (the spec's 1000 us vector).
        byte[] expected = [.. Header, .. ShiftDrag[0].ToBytes(), 0xAB, 0x7F, 0x04, 0xE8, 0x03, 0x00, 0x00, 0x6E, .. ShiftDrag[1].ToBytes()];
        Assert.Equal(1, ProtocolFile.FormatVersion);
        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.Equal([ShiftDrag[0].ToFrame(), new DelayMessage(1000).ToFrame(), ShiftDrag[1].ToFrame()], ProtocolFile.ReadFrames(path));
    }

    // --- timed reading -----------------------------------------------------------------------

    [Fact]
    public void ReadTimedAccumulatesDelaysAndDoesNotYieldThem()
    {
        using var ms = new MemoryStream();
        using (var w = new ProtocolFileWriter(ms, leaveOpen: true))
        {
            w.Write(ShiftDrag[0]);                  // at 0
            w.WriteDelay(Micros(1000));
            w.Write(ShiftDrag[1]);                  // at 1000
            w.WriteDelay(Micros(500));
            w.WriteDelay(Micros(500));              // consecutive records add up
            w.Write(ShiftDrag[2]);                  // at 2000
            w.Write(ShiftDrag[3]);                  // at 2000: no gap
            w.WriteDelay(Micros(3));                // trailing delay with nothing after it
        }
        ms.Position = 0;
        using var r = new ProtocolFileReader(ms, leaveOpen: true);
        var timed = r.ReadTimed().ToArray();

        (TimeSpan At, Message Message)[] expected =
        [
            (Micros(0), ShiftDrag[0]),
            (Micros(1000), ShiftDrag[1]),
            (Micros(2000), ShiftDrag[2]),
            (Micros(2000), ShiftDrag[3]),
        ];
        Assert.Equal(expected, timed);
        Assert.DoesNotContain(timed, t => t.Message.IsFileOnly);
        Assert.Equal(Micros(2003), r.Elapsed);      // includes the trailing delay
        Assert.Equal(8, r.FramesRead);
    }

    [Fact]
    public void ReadTimedIsLazyAndOffsetsAreAbsolute()
    {
        using var ms = new MemoryStream();
        ProtocolFile.Write(ms, [new DelayMessage(10), ShiftDrag[0], new DelayMessage(20), ShiftDrag[1], new DelayMessage(30), ShiftDrag[2]]);
        ms.Position = 0;
        using var r = new ProtocolFileReader(ms, leaveOpen: true);

        Assert.Equal((Micros(10), ShiftDrag[0]), r.ReadTimed().First());
        Assert.Equal(2, r.FramesRead);
        Assert.Equal(Micros(10), r.Elapsed);

        // A second enumeration carries on from where the first stopped, still relative to the file start.
        Assert.Equal((Micros(30), ShiftDrag[1]), r.ReadTimed().First());
        Assert.Equal((Micros(60), ShiftDrag[2]), r.ReadTimed().Single());
    }

    [Fact]
    public void StaticReadTimedMatchesTheReader()
    {
        var path = TempPath();
        ProtocolFile.Write(path, [ShiftDrag[0], new DelayMessage(10), ShiftDrag[1], new DelayMessage(20), ShiftDrag[2]]);
        (TimeSpan At, Message Message)[] expected = [(Micros(0), ShiftDrag[0]), (Micros(10), ShiftDrag[1]), (Micros(30), ShiftDrag[2])];

        Assert.Equal(expected, ProtocolFile.ReadTimed(path));
        using var ms = new MemoryStream(File.ReadAllBytes(path));
        Assert.Equal(expected, ProtocolFile.ReadTimed(ms));
    }

    [Fact]
    public void FileWithoutDelaysReadsTimedAtZero()
    {
        var path = TempPath();
        ProtocolFile.Write(path, ShiftDrag);
        var timed = ProtocolFile.ReadTimed(path);
        Assert.Equal(ShiftDrag, timed.Select(t => t.Message));
        Assert.All(timed, t => Assert.Equal(TimeSpan.Zero, t.At));
    }

    [Fact]
    public void ReadTimedSurfacesMalformedFilesLikeRead()
    {
        var bytes = Header.Concat(new DelayMessage(5).ToBytes()).Concat(new byte[] { 0xAB, 0x7F, 0x04, 0x01 }).ToArray();
        var ex = Assert.Throws<ProtocolFileException>(() => ProtocolFile.ReadTimed(new MemoryStream(bytes)));
        Assert.Contains($"offset {Header.Length + 8}", ex.Message);
    }

    // --- recorder ----------------------------------------------------------------------------

    [Fact]
    public void RecorderWritesTheGapBeforeEachMessage()
    {
        var clock = new FakeTimeProvider();
        using var ms = new MemoryStream();
        using (var recorder = new ProtocolFileRecorder(new ProtocolFileWriter(ms, leaveOpen: true), clock))
        {
            recorder.Record(ShiftDrag[0]);               // first: no delay before it
            clock.Advance(TimeSpan.FromMilliseconds(5));
            recorder.Record(ShiftDrag[1]);
            recorder.Record(ShiftDrag[2]);               // no time passed: no delay record
            clock.Advance(TimeSpan.FromSeconds(2));
            recorder.Record(ShiftDrag[3]);
            Assert.Equal(4, recorder.MessagesRecorded);
            Assert.Equal(6, recorder.Writer.FramesWritten);
        }

        ms.Position = 0;
        Message[] expected = [ShiftDrag[0], new DelayMessage(5_000), ShiftDrag[1], ShiftDrag[2], new DelayMessage(2_000_000), ShiftDrag[3]];
        Assert.Equal(expected, ProtocolFile.Read(ms, leaveOpen: true));

        ms.Position = 0;
        (TimeSpan At, Message Message)[] timed =
        [
            (TimeSpan.Zero, ShiftDrag[0]),
            (Micros(5_000), ShiftDrag[1]),
            (Micros(5_000), ShiftDrag[2]),
            (Micros(2_005_000), ShiftDrag[3]),
        ];
        Assert.Equal(timed, ProtocolFile.ReadTimed(ms));
    }

    [Fact]
    public void RecorderMeasuresFromTheRecordCallNotTheClockStart()
    {
        var clock = new FakeTimeProvider();
        using var ms = new MemoryStream();
        using (var recorder = new ProtocolFileRecorder(new ProtocolFileWriter(ms, leaveOpen: true), clock))
        {
            clock.Advance(TimeSpan.FromMinutes(10));     // time before the first message is not recorded
            recorder.Record(new PingMessage());
            clock.Advance(Micros(7));
            recorder.Record(new PanicMessage());
        }
        ms.Position = 0;
        Assert.Equal([new PingMessage(), new DelayMessage(7), new PanicMessage()], ProtocolFile.Read(ms));
    }

    [Fact]
    public void RecorderRejectsDelaysAndSplitsLongGaps()
    {
        var clock = new FakeTimeProvider();
        var path = TempPath();
        using (var recorder = ProtocolFileRecorder.Create(path, clock))
        {
            Assert.Throws<ArgumentException>(() => recorder.Record(new DelayMessage(1)));
            Assert.Equal(0, recorder.MessagesRecorded);
            recorder.Record(new PingMessage());
            clock.Advance(TimeSpan.FromHours(2));
            recorder.Record(new PanicMessage());
        }

        (TimeSpan At, Message Message)[] expected = [(TimeSpan.Zero, new PingMessage()), (TimeSpan.FromHours(2), new PanicMessage())];
        Assert.Equal(expected, ProtocolFile.ReadTimed(path));
        Assert.Equal(4, ProtocolFile.Read(path).Count);  // ping, two delay records, panic
    }

    [Fact]
    public void RecorderDisposesItsWriterUnlessAskedNotTo()
    {
        var ms = new MemoryStream();
        using (new ProtocolFileRecorder(new ProtocolFileWriter(ms, leaveOpen: true), new FakeTimeProvider(), leaveOpen: true)) { }
        Assert.True(ms.CanWrite);

        using (new ProtocolFileRecorder(new ProtocolFileWriter(ms, leaveOpen: false), new FakeTimeProvider())) { }
        Assert.False(ms.CanWrite);
    }

    private string WriteTemp(byte[] bytes)
    {
        var path = TempPath("raw.msdr");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
