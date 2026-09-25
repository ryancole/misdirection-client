using System.IO.Ports;

namespace Misdirection.Client;

/// <summary>
/// Host side of the misdirection protocol over any duplex <see cref="Stream"/> (normally a serial
/// port). Sends are fire-and-forget as the spec requires; the back-channel is read on a background
/// task and surfaced through events, with <see cref="PingAsync(TimeSpan, CancellationToken)"/> as the
/// one request/response call.
/// </summary>
public sealed class MisdirectionClient : IAsyncDisposable, IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly IDisposable? _owned;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly FrameParser _parser = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;
    private readonly object _pongLock = new();
    private TaskCompletionSource<byte>? _pendingPong;
    private bool _disposed;

    /// <summary>Wraps an already-open duplex stream and starts reading the back-channel immediately.</summary>
    public MisdirectionClient(Stream stream, bool leaveOpen = false)
        : this(stream, leaveOpen, owned: null) { }

    private MisdirectionClient(Stream stream, bool leaveOpen, IDisposable? owned)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite)
            throw new ArgumentException("Stream must be readable and writable.", nameof(stream));

        _stream = stream;
        _leaveOpen = leaveOpen;
        _owned = owned;
        _parser.FrameDiscarded += r => FrameDiscarded?.Invoke(this, r);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Opens a serial port with the protocol's settings (115200 8N1, no flow control) and wraps it.</summary>
    public static MisdirectionClient OpenSerial(string portName, int baudRate = Protocol.DefaultBaudRate)
    {
        var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
        };
        port.Open();
        return new MisdirectionClient(port.BaseStream, leaveOpen: false, owned: port);
    }

    /// <summary>Every decoded device-to-host message, in arrival order.</summary>
    public event EventHandler<Message>? MessageReceived;

    /// <summary>PONG, whether in reply to a PING or unsolicited at firmware boot.</summary>
    public event EventHandler<PongMessage>? PongReceived;

    /// <summary>NACK; arrives asynchronously and only on error.</summary>
    public event EventHandler<NackMessage>? NackReceived;

    /// <summary>
    /// A frame on the back-channel was dropped: malformed, or a file-only record type that is never
    /// valid on the wire (<see cref="FrameDiscardReason.FileOnlyType"/>).
    /// </summary>
    public event EventHandler<FrameDiscardReason>? FrameDiscarded;

    /// <summary>The read loop ended (stream closed or faulted). Null exception means a clean close.</summary>
    public event EventHandler<Exception?>? Closed;

    /// <summary>Completes when the read loop has exited.</summary>
    public Task Completion => _readLoop;

    // --- sending ---------------------------------------------------------------------------

    /// <summary>
    /// Writes one frame. Frames are written whole and never interleaved. Throws
    /// <see cref="ArgumentException"/> for a file-only record type such as <see cref="MessageType.FileDelay"/>,
    /// which must never reach the device.
    /// </summary>
    public async ValueTask SendAsync(Frame frame, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Protocol.IsFileOnly(frame.Type))
            throw new ArgumentException($"{frame.Type} is a file-only record and cannot be sent to the device.", nameof(frame));
        var bytes = frame.Encode();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Encodes and writes one message. Throws <see cref="ArgumentException"/> if <paramref name="message"/>
    /// is file-only (<see cref="Message.IsFileOnly"/>), so a <see cref="DelayMessage"/> read from a file can
    /// never reach the device by mistake; a replayer should wait for <see cref="DelayMessage.Duration"/> instead.
    /// </summary>
    public ValueTask SendAsync(Message message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.IsFileOnly)
            throw new ArgumentException($"{message.Type} is a file-only record and cannot be sent to the device.", nameof(message));
        return SendAsync(message.ToFrame(), ct);
    }

    /// <summary>Release every held key and button. Honored regardless of arm state.</summary>
    public ValueTask PanicAsync(CancellationToken ct = default) => SendAsync(new PanicMessage(), ct);

    public ValueTask KeyDownAsync(byte usage, CancellationToken ct = default) => SendAsync(new KeyDownMessage(usage), ct);

    public ValueTask KeyUpAsync(byte usage, CancellationToken ct = default) => SendAsync(new KeyUpMessage(usage), ct);

    /// <summary>Press and release a key.</summary>
    public async ValueTask TapKeyAsync(byte usage, CancellationToken ct = default)
    {
        await KeyDownAsync(usage, ct).ConfigureAwait(false);
        await KeyUpAsync(usage, ct).ConfigureAwait(false);
    }

    /// <summary>Move the pointer to an absolute screen-pixel position.</summary>
    public ValueTask MouseMoveAsync(ushort x, ushort y, CancellationToken ct = default) =>
        SendAsync(new MouseMoveMessage(x, y), ct);

    /// <summary>Set the absolute button mask.</summary>
    public ValueTask MouseButtonsAsync(MouseButtons buttons, CancellationToken ct = default) =>
        SendAsync(new MouseButtonsMessage(buttons), ct);

    public ValueTask MouseWheelAsync(sbyte vertical, sbyte horizontal = 0, CancellationToken ct = default) =>
        SendAsync(new MouseWheelMessage(vertical, horizontal), ct);

    /// <summary>
    /// Tell the firmware the target resolution. Send before the first move and on any resolution change.
    /// Dimensions outside 128..7680 are rejected here rather than silently clamped by the firmware.
    /// </summary>
    public ValueTask ScreenSizeAsync(ushort width, ushort height, CancellationToken ct = default)
    {
        Validate(width, nameof(width));
        Validate(height, nameof(height));
        return SendAsync(new ScreenSizeMessage(width, height), ct);

        static void Validate(ushort v, string name)
        {
            if (v is < Protocol.MinScreenDimension or > Protocol.MaxScreenDimension)
                throw new ArgumentOutOfRangeException(name, v,
                    $"Must be {Protocol.MinScreenDimension}..{Protocol.MaxScreenDimension}.");
        }
    }

    /// <summary>
    /// Sends PING and waits for the next PONG. Returns the firmware's protocol version. Only one
    /// ping may be outstanding at a time; an unsolicited boot PONG will also satisfy it.
    /// </summary>
    public async Task<byte> PingAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tcs = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pongLock)
        {
            if (_pendingPong is not null)
                throw new InvalidOperationException("A ping is already outstanding.");
            _pendingPong = tcs;
        }

        try
        {
            await SendAsync(new PingMessage(), ct).ConfigureAwait(false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No PONG within {timeout}.");
        }
        finally
        {
            lock (_pongLock)
            {
                if (ReferenceEquals(_pendingPong, tcs)) _pendingPong = null;
            }
        }
    }

    public Task<byte> PingAsync(CancellationToken ct = default) => PingAsync(TimeSpan.FromSeconds(1), ct);

    // --- receiving -------------------------------------------------------------------------

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[256];
        Exception? fault = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var n = await _stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (n == 0) break;
                _parser.Feed(buffer.AsSpan(0, n), Dispatch);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) when (_cts.IsCancellationRequested) { _ = ex; }
        catch (Exception ex) { fault = ex; }
        finally
        {
            FailPendingPong(fault ?? new ObjectDisposedException(nameof(MisdirectionClient)));
            Closed?.Invoke(this, fault);
        }
    }

    private void Dispatch(Frame frame)
    {
        if (!Message.TryDecode(frame, out var message, out _))
        {
            FrameDiscarded?.Invoke(this, FrameDiscardReason.BadLength);
            return;
        }
        if (message.IsFileOnly)
        {
            // FILE_DELAY is a .msdr record, not a wire message; nothing on the device should ever emit it.
            FrameDiscarded?.Invoke(this, FrameDiscardReason.FileOnlyType);
            return;
        }

        MessageReceived?.Invoke(this, message);
        switch (message)
        {
            case PongMessage pong:
                TaskCompletionSource<byte>? tcs;
                lock (_pongLock) { tcs = _pendingPong; _pendingPong = null; }
                tcs?.TrySetResult(pong.Version);
                PongReceived?.Invoke(this, pong);
                break;
            case NackMessage nack:
                NackReceived?.Invoke(this, nack);
                break;
        }
    }

    private void FailPendingPong(Exception ex)
    {
        TaskCompletionSource<byte>? tcs;
        lock (_pongLock) { tcs = _pendingPong; _pendingPong = null; }
        tcs?.TrySetException(ex);
    }

    // --- lifetime --------------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (!_leaveOpen) _stream.Dispose();
        _owned?.Dispose();
        try { await _readLoop.ConfigureAwait(false); } catch { /* surfaced via Closed */ }
        _cts.Dispose();
        _writeLock.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
