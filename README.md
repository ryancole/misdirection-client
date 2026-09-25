# misdirection-client

.NET client for the [misdirection](../misdirection) Teensy HID bridge. Implements the
host side of the wire protocol in `PROTOCOL.md` from that repo: framing, the
HUNT/TYPE/LEN/PAYLOAD/SUM parser, typed messages for both directions, and a
fire-and-forget client over a serial port (or any duplex `Stream`).

## Layout

```
src/
  MisdirectionClient.slnx
  Misdirection.Client/         the library
    Protocol.cs                constants, MessageType / NackReason / MouseButtons
    Frame.cs                   Frame + FrameCodec (encode, checksum)
    FrameParser.cs             incremental parser state machine with resync
    Messages.cs                typed records, ToFrame() / Message.Decode()
    HidUsage.cs                HID keyboard usage codes (what goes on the wire)
    ProtocolFile.cs            .msdr file format: save/load message sequences, delays, recorder, timed playback
    MisdirectionClient.cs      Stream/SerialPort client with events + PingAsync
  Misdirection.Client.Tests/   xunit; TestData/protocol-vectors.json drives the codec tests
etc/
  build.ps1                    dotnet build
  test.ps1                     dotnet test [-Filter name]
  sync-vectors.ps1             copy protocol-vectors.json from the firmware repo
```

## Usage

```csharp
using Misdirection.Client;

await using var client = MisdirectionClient.OpenSerial("COM5");
client.NackReceived += (_, n) => Console.WriteLine($"NACK {n.Reason}");

var version = await client.PingAsync();          // throws TimeoutException if silent
await client.ScreenSizeAsync(1920, 1080);        // before the first move

// shift-drag from (100,100) to (500,400)
await client.KeyDownAsync(HidUsage.LeftShift);
await client.MouseMoveAsync(100, 100);
await client.MouseButtonsAsync(MouseButtons.Left);
await client.MouseMoveAsync(500, 400);
await client.MouseButtonsAsync(MouseButtons.None);
await client.KeyUpAsync(HidUsage.LeftShift);

await client.PanicAsync();                       // release everything
```

Keys are HID usage codes (`HidUsage.A` is `0x04`), not characters. Mapping from
whatever your input source produces is the caller's job, as the protocol intends.

## Saving messages to a file

`ProtocolFile` serializes a message sequence to disk and back. The body of the file
is the wire encoding itself (frames back to back), behind a 6-byte header: magic
`MSDR`, a file-format version, and the protocol version the frames were written with.
Every message round-trips exactly, and the bytes after the header can be replayed to
the device as-is.

```csharp
// whole sequence at once
ProtocolFile.Write("drag.msdr", [
    new KeyDownMessage(HidUsage.LeftShift),
    new MouseMoveMessage(100, 100),
    new KeyUpMessage(HidUsage.LeftShift),
]);
IReadOnlyList<Message> messages = ProtocolFile.Read("drag.msdr");

// streaming, e.g. recording a session as it happens
using (var writer = ProtocolFileWriter.Append("session.msdr"))
    writer.Write(new PingMessage());

using var reader = ProtocolFileReader.Open("session.msdr");
while (reader.TryRead(out var message))
    Console.WriteLine(message);
```

Reading is strict: a bad header, a byte between frames, a bad checksum, an unknown
type or a truncated final frame throws `ProtocolFileException` naming the offset.

### Timing

A file can carry the gaps between messages as `FILE_DELAY` records (type `0x7F`,
`DelayMessage` in code): a u32 of microseconds since the previous frame, in the
normal frame encoding. They exist only in files. `SendAsync` throws
`ArgumentException` if handed one, and one arriving on the back-channel is reported
through `FrameDiscarded` as `FileOnlyType` rather than raised as a message, so
`IsHostToDevice` (true for any type below `0x80`) is not the test for "may go on the
wire"; `Message.IsFileOnly` is.

`WriteDelay` rounds to the nearest microsecond, writes nothing for zero, throws for
negative values, and splits anything over `uint.MaxValue` microseconds (about 71.6
minutes) into consecutive records:

```csharp
using var writer = ProtocolFileWriter.Create("paced.msdr");
writer.Write(new MouseMoveMessage(100, 100));
writer.WriteDelay(TimeSpan.FromMilliseconds(16.7));
writer.Write(new MouseMoveMessage(110, 104));
```

`ProtocolFileRecorder` captures a live session: each `Record` writes the time since
the previous recorded message, then the message. It takes a `TimeProvider`, so tests
can drive it with a fake clock. Record what the host sends, so the file replays as
the same sequence:

```csharp
using var recorder = ProtocolFileRecorder.Create("session.msdr");   // TimeProvider.System

async ValueTask SendAndRecord(Message m)
{
    recorder.Record(m);
    await client.SendAsync(m);
}

await SendAndRecord(new MouseMoveMessage(100, 100));
await SendAndRecord(new MouseButtonsMessage(MouseButtons.Left));
```

For playback, `ReadTimed` yields wire messages only, each with `At`, the running
total of every delay since the start of the file. Schedule against `At` from one
reference time rather than sleeping per gap, so timer overshoot cannot accumulate:

```csharp
await client.ScreenSizeAsync(1920, 1080);
var start = Stopwatch.GetTimestamp();
foreach (var (at, message) in ProtocolFile.ReadTimed("session.msdr"))
{
    var wait = at - Stopwatch.GetElapsedTime(start);
    if (wait > TimeSpan.Zero) await Task.Delay(wait);
    await client.SendAsync(message);
}
```

The plain readers (`Read`, `ReadAll`, `TryRead`) return `DelayMessage` like any
other message, and `ProtocolFileReader.Elapsed` is the sum of delays read so far.

Files without delays are unchanged and the format version stays at 1. A 0.1.x
reader given a file with delays fails with its usual "Unknown message type" error at
the first delay's offset.

## Tests

```bash
etc\test.ps1
```

The vector tests read `TestData/protocol-vectors.json`, a copy of the firmware
repo's generated file. When the protocol changes, regenerate upstream and run
`etc\sync-vectors.ps1`.
