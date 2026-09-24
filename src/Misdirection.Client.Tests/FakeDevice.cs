using System.IO.Pipelines;

namespace Misdirection.Client.Tests;

/// <summary>
/// In-memory stand-in for a serial port: what the client writes lands in <see cref="Sent"/>, and the
/// test pushes device-to-host bytes with <see cref="DeviceSendAsync"/>.
/// </summary>
public sealed class FakeDevice : IAsyncDisposable
{
    private readonly Pipe _hostToDevice = new();
    private readonly Pipe _deviceToHost = new();

    public FakeDevice()
    {
        Stream = new DuplexPipeStream(_deviceToHost.Reader.AsStream(), _hostToDevice.Writer.AsStream());
        Sent = _hostToDevice.Reader.AsStream();
    }

    /// <summary>The stream to hand to the client.</summary>
    public Stream Stream { get; }

    /// <summary>Bytes the client wrote, as the device would read them.</summary>
    public Stream Sent { get; }

    public async Task DeviceSendAsync(Message message)
    {
        await _deviceToHost.Writer.WriteAsync(message.ToBytes());
        await _deviceToHost.Writer.FlushAsync();
    }

    public async Task DeviceSendRawAsync(byte[] bytes)
    {
        await _deviceToHost.Writer.WriteAsync(bytes);
        await _deviceToHost.Writer.FlushAsync();
    }

    /// <summary>Simulates the device side going away (EOF to the client).</summary>
    public Task CloseDeviceAsync() => _deviceToHost.Writer.CompleteAsync().AsTask();

    /// <summary>Reads exactly <paramref name="count"/> bytes of what the client sent.</summary>
    public async Task<byte[]> ReadSentAsync(int count, CancellationToken ct = default)
    {
        var buf = new byte[count];
        await Sent.ReadExactlyAsync(buf, ct);
        return buf;
    }

    /// <summary>Reads the next full frame the client sent and decodes it.</summary>
    public async Task<Message> ReadSentMessageAsync(CancellationToken ct = default)
    {
        var parser = new FrameParser();
        var one = new byte[1];
        while (true)
        {
            await Sent.ReadExactlyAsync(one, ct);
            if (parser.Push(one[0], out var frame)) return Message.Decode(frame!);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _hostToDevice.Writer.CompleteAsync();
        await _deviceToHost.Writer.CompleteAsync();
        await _hostToDevice.Reader.CompleteAsync();
        await _deviceToHost.Reader.CompleteAsync();
    }

    private sealed class DuplexPipeStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => read.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => write.WriteAsync(buffer, ct);
        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { read.Dispose(); write.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
