using System.Text.Json;
using System.Text.Json.Serialization;

namespace Misdirection.Client.Tests;

/// <summary>
/// Loads etc/protocol-vectors.json from the firmware repo (copied into TestData by etc/sync-vectors.ps1).
/// Each vector carries the encoded bytes and the decoded field values, so tests assert both directions.
/// </summary>
public static class ProtocolVectors
{
    public sealed record VectorFile(int ProtocolVersion, int Sof, List<Vector> Vectors);

    public sealed record Vector(
        string Name,
        string Direction,
        int Type,
        string TypeName,
        Dictionary<string, int> Fields,
        string Hex,
        int[] Bytes)
    {
        /// <summary>The encoded frame. (System.Text.Json would read a <c>byte[]</c> as base64.)</summary>
        public byte[] Wire => Bytes.Select(b => checked((byte)b)).ToArray();

        public override string ToString() => Name;
    }

    private static readonly Lazy<VectorFile> File = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "protocol-vectors.json");
        var json = System.IO.File.ReadAllText(path);
        return JsonSerializer.Deserialize<VectorFile>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        }) ?? throw new InvalidOperationException("Empty vector file.");
    });

    public static VectorFile All => File.Value;

    public static IEnumerable<object[]> AsTheoryData() => All.Vectors.Select(v => new object[] { v });

    /// <summary>Builds the typed message a vector describes from its decoded fields.</summary>
    public static Message ToMessage(Vector v)
    {
        var f = v.Fields;
        return v.TypeName switch
        {
            "PANIC" => new PanicMessage(),
            "KEY_DOWN" => new KeyDownMessage((byte)f["usage"]),
            "KEY_UP" => new KeyUpMessage((byte)f["usage"]),
            "MOUSE_MOVE" => new MouseMoveMessage((ushort)f["x"], (ushort)f["y"]),
            "MOUSE_BTN" => new MouseButtonsMessage((MouseButtons)f["mask"]),
            "MOUSE_WHEEL" => new MouseWheelMessage((sbyte)f["vert"], (sbyte)f["horiz"]),
            "SCREEN_SIZE" => new ScreenSizeMessage((ushort)f["width"], (ushort)f["height"]),
            "PING" => new PingMessage(),
            "PONG" => new PongMessage((byte)f["version"]),
            "NACK" => new NackMessage((NackReason)f["reason"]),
            _ => throw new ArgumentException($"Vector file has a type this client does not know: {v.TypeName}"),
        };
    }
}
