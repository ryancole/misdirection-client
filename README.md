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
    ProtocolFile.cs            .msdr file format: save/load message sequences, streaming reader/writer
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

## Tests

```bash
etc\test.ps1
```

The vector tests read `TestData/protocol-vectors.json`, a copy of the firmware
repo's generated file. When the protocol changes, regenerate upstream and run
`etc\sync-vectors.ps1`.
