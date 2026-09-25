namespace Misdirection.Client.Tests;

public class MisdirectionClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task HelpersEmitSpecFrames()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        await client.ScreenSizeAsync(1920, 1080);
        await client.KeyDownAsync(HidUsage.LeftShift);
        await client.MouseButtonsAsync(MouseButtons.Left);
        await client.MouseMoveAsync(960, 540);
        await client.MouseWheelAsync(-1);
        await client.MouseMoveRelAsync(10, -5);
        await client.MouseButtonsAsync(MouseButtons.None);
        await client.KeyUpAsync(HidUsage.LeftShift);
        await client.PanicAsync();

        byte[] expected =
        [
            0xAB, 0x06, 0x04, 0x80, 0x07, 0x38, 0x04, 0xCD,
            0xAB, 0x01, 0x01, 0xE1, 0xE3,
            0xAB, 0x04, 0x01, 0x01, 0x06,
            0xAB, 0x03, 0x04, 0xC0, 0x03, 0x1C, 0x02, 0xE8,
            0xAB, 0x05, 0x02, 0xFF, 0x00, 0x06,
            0xAB, 0x08, 0x04, 0x0A, 0x00, 0xFB, 0xFF, 0x10,
            0xAB, 0x04, 0x01, 0x00, 0x05,
            0xAB, 0x02, 0x01, 0xE1, 0xE4,
            0xAB, 0x00, 0x00, 0x00,
        ];
        Assert.Equal(expected, await device.ReadSentAsync(expected.Length));
    }

    [Fact]
    public async Task TapKeySendsDownThenUp()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        await client.TapKeyAsync(HidUsage.A);

        Assert.Equal(new KeyDownMessage(HidUsage.A), await device.ReadSentMessageAsync());
        Assert.Equal(new KeyUpMessage(HidUsage.A), await device.ReadSentMessageAsync());
    }

    [Fact]
    public async Task PingReturnsFirmwareVersionFromPong()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        var ping = client.PingAsync(Timeout);
        Assert.Equal(new PingMessage(), await device.ReadSentMessageAsync());
        await device.DeviceSendAsync(new PongMessage(1));

        Assert.Equal(1, await ping);
    }

    [Fact]
    public async Task PingTimesOutWithoutPong()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        await Assert.ThrowsAsync<TimeoutException>(() => client.PingAsync(TimeSpan.FromMilliseconds(50)));
        // A second ping is allowed after the first one is cleaned up.
        var second = client.PingAsync(Timeout);
        await device.ReadSentMessageAsync();
        await device.ReadSentMessageAsync();
        await device.DeviceSendAsync(new PongMessage(1));
        Assert.Equal(1, await second);
    }

    [Fact]
    public async Task ConcurrentPingsAreRejected()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        var first = client.PingAsync(Timeout);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync(Timeout));
        await device.DeviceSendAsync(new PongMessage(1));
        await first;
    }

    [Fact]
    public async Task NackAndUnsolicitedPongRaiseEvents()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        var nack = new TaskCompletionSource<NackMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pong = new TaskCompletionSource<PongMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var all = new List<Message>();
        client.NackReceived += (_, m) => nack.TrySetResult(m);
        client.PongReceived += (_, m) => pong.TrySetResult(m);
        client.MessageReceived += (_, m) => { lock (all) all.Add(m); };

        await device.DeviceSendAsync(new PongMessage(1)); // boot pong
        await device.DeviceSendAsync(new NackMessage(NackReason.Disarmed));

        Assert.Equal(new PongMessage(1), await pong.Task.WaitAsync(Timeout));
        Assert.Equal(NackReason.Disarmed, (await nack.Task.WaitAsync(Timeout)).Reason);
        lock (all) Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task CorruptBackChannelFrameIsReportedNotFatal()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        var discarded = new TaskCompletionSource<FrameDiscardReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pong = new TaskCompletionSource<PongMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameDiscarded += (_, r) => discarded.TrySetResult(r);
        client.PongReceived += (_, m) => pong.TrySetResult(m);

        await device.DeviceSendRawAsync([0xAB, 0x80, 0x01, 0x01, 0x00]); // bad sum
        await device.DeviceSendAsync(new PongMessage(1));

        Assert.Equal(FrameDiscardReason.BadChecksum, await discarded.Task.WaitAsync(Timeout));
        Assert.Equal(new PongMessage(1), await pong.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task SendRejectsFileOnlyMessages()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        var delay = new DelayMessage(1000);
        Assert.True(delay.IsHostToDevice);   // the type range alone would let it through
        Assert.True(delay.IsFileOnly);
        await Assert.ThrowsAsync<ArgumentException>(async () => await client.SendAsync(delay));
        await Assert.ThrowsAsync<ArgumentException>(async () => await client.SendAsync(delay.ToFrame()));

        // Nothing reached the device: the next frame it sees is the panic.
        await client.PanicAsync();
        Assert.Equal(new PanicMessage(), await device.ReadSentMessageAsync());
    }

    [Fact]
    public async Task InboundFileDelayIsDiscardedNotDispatched()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        var discarded = new TaskCompletionSource<FrameDiscardReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pong = new TaskCompletionSource<PongMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var all = new List<Message>();
        client.FrameDiscarded += (_, r) => discarded.TrySetResult(r);
        client.PongReceived += (_, m) => pong.TrySetResult(m);
        client.MessageReceived += (_, m) => { lock (all) all.Add(m); };

        await device.DeviceSendRawAsync([0xAB, 0x7F, 0x04, 0xE8, 0x03, 0x00, 0x00, 0x6E]); // well-formed FILE_DELAY 1000 us
        await device.DeviceSendAsync(new PongMessage(1));

        Assert.Equal(FrameDiscardReason.FileOnlyType, await discarded.Task.WaitAsync(Timeout));
        Assert.Equal(new PongMessage(1), await pong.Task.WaitAsync(Timeout));
        lock (all) Assert.Equal([new PongMessage(1)], all);
    }

    [Fact]
    public async Task ScreenSizeRejectsOutOfRangeDimensions()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await client.ScreenSizeAsync(127, 1080));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await client.ScreenSizeAsync(1920, 7681));
    }

    [Fact]
    public async Task DeviceEofRaisesClosedAndFailsPendingPing()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Closed += (_, ex) => closed.TrySetResult(ex);

        var ping = client.PingAsync(Timeout);
        await device.ReadSentMessageAsync();
        await device.CloseDeviceAsync();

        Assert.Null(await closed.Task.WaitAsync(Timeout));
        await Assert.ThrowsAnyAsync<Exception>(() => ping);
        await client.Completion.WaitAsync(Timeout);
    }

    [Fact]
    public async Task SendAfterDisposeThrows()
    {
        await using var device = new FakeDevice();
        var client = new MisdirectionClient(device.Stream, leaveOpen: true);
        await client.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await client.PingAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await client.PanicAsync());
    }

    [Fact]
    public void RejectsNonDuplexStream()
    {
        using var readOnly = new MemoryStream([], writable: false);
        Assert.Throws<ArgumentException>(() => new MisdirectionClient(readOnly));
    }
}
