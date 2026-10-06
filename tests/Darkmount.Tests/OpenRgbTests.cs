using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Darkmount.Keyboard.Lamps;
using Darkmount.Keyboard.Lamps.OpenRgb;

namespace Darkmount.Tests;

public class OpenRgbTests
{
    // ---------------------------------------------------------------- synthetic packet builders

    /// <summary>Little-endian writer for building OpenRGB blocks by hand.</summary>
    sealed class W
    {
        readonly List<byte> _b = [];
        public int Length => _b.Count;
        public W U16(int v) { _b.Add((byte)v); _b.Add((byte)(v >> 8)); return this; }
        public W U32(uint v) { for (var i = 0; i < 4; i++) _b.Add((byte)(v >> (8 * i))); return this; }
        public W I32(int v) => U32((uint)v);
        public W Bytes(byte[] b) { _b.AddRange(b); return this; }
        public W Str(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            U16(bytes.Length + 1);
            _b.AddRange(bytes);
            _b.Add(0);
            return this;
        }
        public byte[] ToArray() => [.. _b];
    }

    sealed record ModeSpec(string Name, uint Flags, uint ColorMode, uint[] Colors);

    sealed record ZoneSpec(string Name, int Type, uint Count, uint[,]? Matrix = null, int Segments = 0);

    static byte[] ModeBlock(ModeSpec m, uint v)
    {
        var w = new W().Str(m.Name).I32(7).U32(m.Flags).U32(1).U32(10);
        if (v >= 3) w.U32(0).U32(100);
        w.U32(1).U32(3).U32(5);
        if (v >= 3) w.U32(80);
        w.U32(0).U32(m.ColorMode).U16(m.Colors.Length);
        foreach (var c in m.Colors) w.U32(c);
        return w.ToArray();
    }

    static byte[] ControllerData(uint v, int type, string name, ModeSpec[] modes, int activeMode, ZoneSpec[] zones, string[] leds)
    {
        var w = new W().I32(type).Str(name);
        if (v >= 1) w.Str("Vendor X");
        w.Str("Desc").Str("1.0").Str("SN123").Str("HID: /dev/x");
        w.U16(modes.Length).I32(activeMode);
        foreach (var m in modes) w.Bytes(ModeBlock(m, v));
        w.U16(zones.Length);
        foreach (var z in zones)
        {
            w.Str(z.Name).I32(z.Type).U32(0).U32(z.Count).U32(z.Count);
            if (z.Matrix is { } mx)
            {
                int h = mx.GetLength(0), wd = mx.GetLength(1);
                w.U16(8 + 4 * h * wd).U32((uint)h).U32((uint)wd);
                for (var r = 0; r < h; r++) for (var c = 0; c < wd; c++) w.U32(mx[r, c]);
            }
            else w.U16(0);
            if (v >= 4)
            {
                w.U16(z.Segments);
                for (var s = 0; s < z.Segments; s++) w.Str($"Seg {s}").I32(1).U32(0).U32(1);
            }
            if (v >= 5) w.U32(0);
        }
        w.U16(leds.Length);
        foreach (var l in leds) w.Str(l).U32(0);
        w.U16(leds.Length);
        foreach (var _ in leds) w.U32(0x00FF00);
        if (v >= 5) w.U16(1).Str("Alt").U32(0);

        var body = w.ToArray();
        return new W().U32((uint)(body.Length + 4)).Bytes(body).ToArray();
    }

    const uint X = OpenRgbProtocol.NoLed;

    static readonly ModeSpec Direct = new("Direct", 1u << 5, 1, []);
    static readonly ModeSpec Breathing = new("Breathing", 1u << 0 | 1u << 2, 2, [0x0000FF, 0x00FF00]);

    static readonly ZoneSpec MatrixZone = new("Keys", OpenRgbZone.MatrixType, 5, new uint[,] { { 0, 1, 2 }, { 3, X, 4 } }, Segments: 1);
    static readonly ZoneSpec LogoZone = new("Logo", OpenRgbZone.LinearType, 3);
    static readonly string[] SampleLeds = ["Key: Escape", "Key: A", "Key: Left Shift", "Key: Space", "Key: Right Fn", "Logo 1", "Logo 2", "Logo 3"];

    static byte[] Sample(uint v, int type = 5) =>
        ControllerData(v, type, "Test Keyboard", [Direct, Breathing], 1, [MatrixZone, LogoZone], SampleLeds);

    // ---------------------------------------------------------------- parser

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    public void ParsesControllerAtEachVersion(uint v)
    {
        var c = OpenRgbProtocol.ParseController(Sample(v), 3, v);

        Assert.Equal(3, c.Index);
        Assert.Equal(OpenRgbDeviceType.Keyboard, c.Type);
        Assert.True(c.IsKeyboardLike);
        Assert.Equal("Test Keyboard", c.Name);
        Assert.Equal(v >= 1 ? "Vendor X" : "", c.Vendor);
        Assert.Equal("Desc", c.Description);
        Assert.Equal("SN123", c.Serial);
        Assert.Equal("HID: /dev/x", c.Location);

        Assert.Equal(2, c.Modes.Count);
        Assert.Equal(1, c.ActiveMode);
        Assert.Equal("Direct", c.Modes[0].Name);
        Assert.True(c.Modes[0].HasPerLedColor);
        Assert.False(c.Modes[1].HasPerLedColor);
        Assert.Equal(2u, c.Modes[1].ColorMode);
        Assert.Equal(ModeBlock(Direct, v), c.Modes[0].RawBlock);
        Assert.Equal(ModeBlock(Breathing, v), c.Modes[1].RawBlock);

        Assert.Equal(2, c.Zones.Count);
        var keys = c.Zones[0];
        Assert.Equal(("Keys", OpenRgbZone.MatrixType, 0, 5, 2, 3), (keys.Name, keys.Type, keys.StartLed, keys.LedCount, keys.MatrixHeight, keys.MatrixWidth));
        Assert.Equal([0u, 1, 2, 3, X, 4], keys.Matrix);
        var logo = c.Zones[1];
        Assert.Equal(("Logo", OpenRgbZone.LinearType, 5, 3, 0, 0), (logo.Name, logo.Type, logo.StartLed, logo.LedCount, logo.MatrixHeight, logo.MatrixWidth));
        Assert.Empty(logo.Matrix);

        Assert.Equal(SampleLeds, c.LedNames);
        Assert.Equal(8, c.LedCount);
    }

    [Fact]
    public void LaptopAndMonitorOnlyExistFromVersion5()
    {
        Assert.Equal(OpenRgbDeviceType.Laptop, OpenRgbProtocol.ParseController(Sample(5, 19), 0, 5).Type);
        Assert.Equal(OpenRgbDeviceType.Monitor, OpenRgbProtocol.ParseController(Sample(5, 20), 0, 5).Type);
        Assert.Equal(OpenRgbDeviceType.Unknown, OpenRgbProtocol.ParseController(Sample(4, 19), 0, 4).Type);
        Assert.Equal(OpenRgbDeviceType.Unknown, OpenRgbProtocol.ParseController(Sample(4, 21), 0, 4).Type);
        Assert.Equal(OpenRgbDeviceType.Keypad, OpenRgbProtocol.ParseController(Sample(4, 18), 0, 4).Type);
        Assert.Equal(OpenRgbDeviceType.Unknown, OpenRgbProtocol.ParseController(Sample(5, 99), 0, 5).Type);
        Assert.Equal(OpenRgbDeviceType.Unknown, OpenRgbProtocol.ParseController(Sample(5, -1), 0, 5).Type);
        Assert.True(OpenRgbProtocol.ParseController(Sample(5, 19), 0, 5).IsKeyboardLike);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    public void TruncatedDataThrowsFormatException(uint v)
    {
        var full = Sample(v);
        for (var n = 0; n < full.Length; n++)
        {
            // data_size still claims the full length.
            Assert.Throws<FormatException>(() => OpenRgbProtocol.ParseController(full.AsSpan(0, n), 0, v));
            if (n < 4) continue;
            // data_size rewritten to match, so the parser runs into the cut mid-block.
            var cut = full[..n];
            BinaryPrimitives.WriteUInt32LittleEndian(cut, (uint)n);
            Assert.Throws<FormatException>(() => OpenRgbProtocol.ParseController(cut, 0, v));
        }
    }

    [Fact]
    public void GarbageNeverThrowsAnythingButFormatException()
    {
        var rng = new Random(1234);
        var valid = Sample(5);
        for (var i = 0; i < 3000; i++)
        {
            var data = (byte[])valid.Clone();
            for (var k = 0; k < 1 + rng.Next(6); k++) data[4 + rng.Next(data.Length - 4)] = (byte)rng.Next(256);
            try { OpenRgbProtocol.ParseController(data, 0, (uint)rng.Next(6)); }
            catch (FormatException) { }
        }
        for (var i = 0; i < 2000; i++)
        {
            var data = new byte[rng.Next(4, 200)];
            rng.NextBytes(data);
            BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length);
            try { OpenRgbProtocol.ParseController(data, 0, 5); }
            catch (FormatException) { }
        }
    }

    // ---------------------------------------------------------------- HID usages

    [Theory]
    [InlineData("Key: A", 0x04)]
    [InlineData("Key: Z", 0x1D)]
    [InlineData("key: q", 0x14)]
    [InlineData("Key: 1", 0x1E)]
    [InlineData("Key: 0", 0x27)]
    [InlineData("Key: Enter", 0x28)]
    [InlineData("Key: Enter (ISO)", 0x28)]
    [InlineData("Key: Escape", 0x29)]
    [InlineData("Key: Backspace", 0x2A)]
    [InlineData("Key: Tab", 0x2B)]
    [InlineData("Key: Space", 0x2C)]
    [InlineData("Key: -", 0x2D)]
    [InlineData("Key: =", 0x2E)]
    [InlineData("Key: [", 0x2F)]
    [InlineData("Key: ]", 0x30)]
    [InlineData("Key: \\", 0x31)]
    [InlineData("Key: \\ (ANSI)", 0x31)]
    [InlineData("Key: #", 0x32)]
    [InlineData("Key: ;", 0x33)]
    [InlineData("Key: '", 0x34)]
    [InlineData("Key: `", 0x35)]
    [InlineData("Key: ,", 0x36)]
    [InlineData("Key: .", 0x37)]
    [InlineData("Key: /", 0x38)]
    [InlineData("Key: Caps Lock", 0x39)]
    [InlineData("Key: F1", 0x3A)]
    [InlineData("Key: F12", 0x45)]
    [InlineData("Key: F13", 0x68)]
    [InlineData("Key: F24", 0x73)]
    [InlineData("Key: Print Screen", 0x46)]
    [InlineData("Key: Scroll Lock", 0x47)]
    [InlineData("Key: Pause/Break", 0x48)]
    [InlineData("Key: Insert", 0x49)]
    [InlineData("Key: Home", 0x4A)]
    [InlineData("Key: Page Up", 0x4B)]
    [InlineData("Key: Delete", 0x4C)]
    [InlineData("Key: End", 0x4D)]
    [InlineData("Key: Page Down", 0x4E)]
    [InlineData("Key: Right Arrow", 0x4F)]
    [InlineData("Key: Left Arrow", 0x50)]
    [InlineData("Key: Down Arrow", 0x51)]
    [InlineData("Key: Up Arrow", 0x52)]
    [InlineData("Key: Num Lock", 0x53)]
    [InlineData("Key: Number Pad /", 0x54)]
    [InlineData("Key: Number Pad *", 0x55)]
    [InlineData("Key: Number Pad -", 0x56)]
    [InlineData("Key: Number Pad +", 0x57)]
    [InlineData("Key: Number Pad Enter", 0x58)]
    [InlineData("Key: Number Pad 1", 0x59)]
    [InlineData("Key: Number Pad 9", 0x61)]
    [InlineData("Key: Number Pad 0", 0x62)]
    [InlineData("Key: Number Pad .", 0x63)]
    [InlineData("Key: \\ (ISO)", 0x64)]
    [InlineData("Key: Menu", 0x65)]
    [InlineData("Key: Left Control", 0xE0)]
    [InlineData("Key: Left Shift", 0xE1)]
    [InlineData("Key: Left Alt", 0xE2)]
    [InlineData("Key: Left Windows", 0xE3)]
    [InlineData("Key: Right Control", 0xE4)]
    [InlineData("Key: Right Shift", 0xE5)]
    [InlineData("Key: Right Alt", 0xE6)]
    [InlineData("Key: Right Windows", 0xE7)]
    public void MapsKeyNamesToHidUsages(string name, int usage) => Assert.Equal(usage, OpenRgbLayout.HidUsage(name));

    [Theory]
    [InlineData("Key: Left Fn")]
    [InlineData("Key: Right Fn")]
    [InlineData("Key: Media Play/Pause")]
    [InlineData("Logo")]
    [InlineData("A")]
    [InlineData("")]
    [InlineData("Key:")]
    public void UnknownNamesHaveNoUsage(string name) => Assert.Null(OpenRgbLayout.HidUsage(name));

    // ---------------------------------------------------------------- layout

    static OpenRgbController Ctl(OpenRgbDeviceType type, OpenRgbZone[] zones, string[] leds) =>
        new(0, type, "T", "", "", "", "", [], 0, zones, leds);

    static void AssertFinite(IReadOnlyList<LampPoint> points)
    {
        foreach (var p in points)
        {
            Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y), $"lamp {p.LampId}: {p.X},{p.Y}");
            Assert.InRange(p.X, 0, 1);
            Assert.InRange(p.Y, 0, 1);
        }
    }

    [Fact]
    public void MatrixKeyboardLayout()
    {
        var c = OpenRgbProtocol.ParseController(Sample(5), 0, 5);
        var pts = OpenRgbLayout.Build(c);
        AssertFinite(pts);

        Assert.Equal(Enumerable.Range(0, 8), pts.Select(p => p.LampId));
        // 2×3 matrix rows 0..1, logo row 2 → Y 0, 0.5, 1. X over 3 columns → 0, 0.5, 1.
        Assert.Equal((0.0, 0.0), (pts[0].X, pts[0].Y)); // Escape (0,0)
        Assert.Equal((0.5, 0.0), (pts[1].X, pts[1].Y)); // A (1,0)
        Assert.Equal((1.0, 0.0), (pts[2].X, pts[2].Y)); // Left Shift (2,0)
        Assert.Equal((0.0, 0.5), (pts[3].X, pts[3].Y)); // Space (0,1)
        Assert.Equal((1.0, 0.5), (pts[4].X, pts[4].Y)); // Right Fn (2,1)
        Assert.Equal([(0.0, 1.0), (0.5, 1.0), (1.0, 1.0)], pts.Skip(5).Select(p => (p.X, p.Y)));

        Assert.All(pts.Take(5), p => Assert.True(p.IsKey));
        Assert.All(pts.Skip(5), p => Assert.False(p.IsKey));
        Assert.Equal(DarkmountKeys.ByUsage[0x29].Id, pts[0].KeyId);
        Assert.Equal(DarkmountKeys.ByUsage[0x04].Id, pts[1].KeyId);
        Assert.Equal(DarkmountKeys.ByUsage[0xE1].Id, pts[2].KeyId);
        Assert.Equal(DarkmountKeys.ByUsage[0x2C].Id, pts[3].KeyId);
        Assert.Equal(0, pts[4].KeyId); // Fn has no usage
        Assert.All(pts, p => Assert.Equal(0, p.Edge));
    }

    static readonly OpenRgbZone[] LaptopZones =
    [
        new("Keyboard", OpenRgbZone.LinearType, 0, 4, 0, 0, []),
        new("Logo", OpenRgbZone.SingleType, 4, 1, 0, 0, []),
        new("Light Bar", OpenRgbZone.LinearType, 5, 3, 0, 0, []),
        new("Keyboard Edge", OpenRgbZone.LinearType, 8, 2, 0, 0, []),
    ];

    static readonly string[] LaptopLeds = ["Keyboard 1", "Keyboard 2", "Keyboard 3", "Keyboard 4", "Logo", "Bar 1", "Bar 2", "Bar 3", "Edge 1", "Edge 2"];

    [Fact]
    public void LaptopKeyboardZonesBecomeKeys()
    {
        var pts = OpenRgbLayout.Build(Ctl(OpenRgbDeviceType.Laptop, LaptopZones, LaptopLeds));
        AssertFinite(pts);
        Assert.Equal(10, pts.Count);
        Assert.Equal([true, true, true, true, false, false, false, false, true, true], pts.Select(p => p.IsKey));
        Assert.All(pts, p => Assert.Equal(0, p.KeyId));

        // One row per zone: Y 0, 1/3, 2/3, 1. Keyboard LEDs spread 0..1, the logo centred.
        Assert.Equal([0.0, 1 / 3.0, 2 / 3.0, 1.0], pts.Take(4).Select(p => p.X), new Tol());
        Assert.All(pts.Take(4), p => Assert.Equal(0, p.Y));
        Assert.Equal((0.5, 1 / 3.0), (pts[4].X, pts[4].Y), new TolPair());
        Assert.Equal((0.0, 1.0), (pts[8].X, pts[8].Y));
        Assert.Equal((1.0, 1.0), (pts[9].X, pts[9].Y));

        // The same zones on a mouse aren't keys.
        Assert.All(OpenRgbLayout.Build(Ctl(OpenRgbDeviceType.Mouse, LaptopZones, LaptopLeds)), p => Assert.False(p.IsKey));
    }

    [Fact]
    public void SingleLedDeviceSitsInTheCentre()
    {
        var pts = OpenRgbLayout.Build(Ctl(OpenRgbDeviceType.Laptop, [new("Keyboard", OpenRgbZone.SingleType, 0, 1, 0, 0, [])], ["Keyboard"]));
        Assert.Equal(new LampPoint(0, 0.5, 0.5, true), Assert.Single(pts));

        var bare = OpenRgbLayout.Build(Ctl(OpenRgbDeviceType.Light, [], ["Bulb"]));
        Assert.Equal(new LampPoint(0, 0.5, 0.5, false), Assert.Single(bare));
    }

    [Fact]
    public void LayoutNeverProducesNaN()
    {
        var odd = new[]
        {
            Ctl(OpenRgbDeviceType.Keyboard, [], []),
            Ctl(OpenRgbDeviceType.Keyboard, [], ["Key: A", "Key: B", "x"]),
            // Zone claims more LEDs than exist, negative start, a matrix with out-of-range entries, an empty matrix.
            Ctl(OpenRgbDeviceType.Keyboard,
            [
                new("Big", OpenRgbZone.LinearType, 0, 50, 0, 0, []),
                new("Neg", OpenRgbZone.LinearType, -3, 2, 0, 0, []),
            ], ["a", "b", "c"]),
            Ctl(OpenRgbDeviceType.Keyboard,
            [
                new("M", OpenRgbZone.MatrixType, 0, 3, 1, 4, [7, X, 0, 99]),
                new("E", OpenRgbZone.MatrixType, 3, 1, 0, 0, []),
            ], ["Key: A", "Key: B", "Key: C", "Key: D"]),
            Ctl(OpenRgbDeviceType.Keyboard, [new("Col", OpenRgbZone.MatrixType, 0, 3, 3, 1, [0, 1, 2])], ["1", "2", "3"]),
            OpenRgbProtocol.ParseController(Sample(0), 0, 0),
        };
        foreach (var c in odd)
        {
            var pts = OpenRgbLayout.Build(c);
            Assert.Equal(c.LedCount, pts.Count);
            AssertFinite(pts);
        }

        // A single column matrix: X = 0.5, Y spread.
        var col = OpenRgbLayout.Build(odd[4]);
        Assert.Equal([(0.5, 0.0), (0.5, 0.5), (0.5, 1.0)], col.Select(p => (p.X, p.Y)));
    }

    sealed class Tol : IEqualityComparer<double>
    {
        public bool Equals(double a, double b) => Math.Abs(a - b) < 1e-9;
        public int GetHashCode(double d) => 0;
    }

    sealed class TolPair : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) a, (double, double) b) => Math.Abs(a.Item1 - b.Item1) < 1e-9 && Math.Abs(a.Item2 - b.Item2) < 1e-9;
        public int GetHashCode((double, double) d) => 0;
    }

    // ---------------------------------------------------------------- encoders

    [Fact]
    public void ColorEncodingScalesByIntensity()
    {
        Assert.Equal(0x00030201u, OpenRgbProtocol.EncodeColor(new LampColor(1, 2, 3)));
        Assert.Equal(0u, OpenRgbProtocol.EncodeColor(LampColor.Off));
        Assert.Equal(0x00000080u, OpenRgbProtocol.EncodeColor(new LampColor(255, 0, 0, 128)));
    }

    // ---------------------------------------------------------------- client against a fake server

    sealed class FakeServer : IDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public FakeServer() => _listener.Start();

        public Task Run(Action<NetworkStream> script) => Task.Run(() =>
        {
            using var client = _listener.AcceptTcpClient();
            using var stream = client.GetStream();
            stream.ReadTimeout = stream.WriteTimeout = 10_000;
            script(stream);
        });

        public void Dispose() => _listener.Stop();
    }

    static (uint Dev, uint Id, byte[] Payload, byte[] Raw) ReadPacket(NetworkStream s)
    {
        var header = new byte[16];
        s.ReadExactly(header);
        Assert.Equal("ORGB"u8.ToArray(), header[..4]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        var payload = new byte[size];
        s.ReadExactly(payload);
        return (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)), payload, [.. header, .. payload]);
    }

    static void WritePacket(NetworkStream s, uint dev, uint id, byte[] payload) =>
        s.Write(new W().Bytes("ORGB"u8.ToArray()).U32(dev).U32(id).U32((uint)payload.Length).Bytes(payload).ToArray());

    static byte[] U32(uint v) => new W().U32(v).ToArray();

    /// <summary>Server side of the handshake: expects the client name, then answers the version request.</summary>
    static void ServeHandshake(NetworkStream s, string name, uint? serverVersion)
    {
        var hello = ReadPacket(s);
        Assert.Equal((0u, 50u), (hello.Dev, hello.Id));
        Assert.Equal(Encoding.UTF8.GetBytes(name + "\0"), hello.Payload);
        var ver = ReadPacket(s);
        Assert.Equal((0u, 40u), (ver.Dev, ver.Id));
        Assert.Equal(U32(OpenRgbProtocol.ClientVersion), ver.Payload);
        if (serverVersion is { } v) WritePacket(s, 0, 40, U32(v));
    }

    [Fact]
    public async Task ClientTalksToFakeServer()
    {
        using var server = new FakeServer();
        var raw = new List<byte[]>();
        var serverTask = server.Run(s =>
        {
            ServeHandshake(s, "TestClient", serverVersion: 4);

            // GetControllers: DEVICE_LIST_UPDATED arrives in place of the count reply, then the real reply.
            var count = ReadPacket(s);
            Assert.Equal((0u, 0u, 0), (count.Dev, count.Id, count.Payload.Length));
            WritePacket(s, 0, 100, []);
            WritePacket(s, 0, 0, U32(2));

            var req0 = ReadPacket(s);
            Assert.Equal((0u, 1u), (req0.Dev, req0.Id));
            Assert.Equal(U32(4), req0.Payload);
            WritePacket(s, 0, 100, []);
            WritePacket(s, 0, 1, Sample(4));

            var req1 = ReadPacket(s);
            Assert.Equal((1u, 1u), (req1.Dev, req1.Id));
            WritePacket(s, 1, 1, Sample(4)[..40]); // truncated → skipped

            // Second GetControllers: nothing changed.
            Assert.Equal(0u, ReadPacket(s).Id);
            WritePacket(s, 0, 0, U32(0));

            for (var i = 0; i < 3; i++) raw.Add(ReadPacket(s).Raw);
        });

        using var client = OpenRgbClient.TryConnect("127.0.0.1", server.Port, "TestClient");
        Assert.NotNull(client);
        Assert.Equal(4u, client.ProtocolVersion);
        Assert.True(client.Connected);
        Assert.False(client.DeviceListChanged);

        var controllers = client.GetControllers();
        var c = Assert.Single(controllers);
        Assert.Equal((0, "Test Keyboard", 8), (c.Index, c.Name, c.LedCount));
        Assert.True(client.DeviceListChanged); // arrived while the list was being fetched

        Assert.Empty(client.GetControllers());
        Assert.False(client.DeviceListChanged);

        client.SetCustomMode(2);
        client.UpdateLeds(1, [new LampColor(0x11, 0x22, 0x33), new LampColor(0xFF, 0x00, 0x80)]);
        var modeBlock = c.Modes[1].RawBlock;
        client.UpdateMode(0, 1, modeBlock);

        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new W().Bytes("ORGB"u8.ToArray()).U32(2).U32(1100).U32(0).ToArray(), raw[0]);
        Assert.Equal(new W().Bytes("ORGB"u8.ToArray()).U32(1).U32(1050).U32(14)
            .U32(14).U16(2).U32(0x00332211).U32(0x008000FF).ToArray(), raw[1]);
        Assert.Equal(new W().Bytes("ORGB"u8.ToArray()).U32(0).U32(1101).U32((uint)(8 + modeBlock.Length))
            .U32((uint)(8 + modeBlock.Length)).I32(1).Bytes(modeBlock).ToArray(), raw[2]);
    }

    [Fact]
    public async Task DeviceListUpdatedIsNoticedWhileOnlyStreaming()
    {
        using var server = new FakeServer();
        using var sent = new ManualResetEventSlim();
        var serverTask = server.Run(s =>
        {
            ServeHandshake(s, "OverMount", serverVersion: 9);
            WritePacket(s, 0, 100, []);
            sent.Set();
            Assert.Equal(1050u, ReadPacket(s).Id);
        });

        using var client = OpenRgbClient.TryConnect(port: server.Port);
        Assert.NotNull(client);
        Assert.Equal(OpenRgbProtocol.ClientVersion, client.ProtocolVersion);
        Assert.True(sent.Wait(5000));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!client.DeviceListChanged && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.True(client.DeviceListChanged);
        client.UpdateLeds(0, [new LampColor(1, 2, 3)]);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task SilentServerNegotiatesVersion0()
    {
        using var server = new FakeServer();
        var serverTask = server.Run(s =>
        {
            ServeHandshake(s, "OverMount", serverVersion: null);
            Assert.Equal(0u, ReadPacket(s).Id);
            WritePacket(s, 0, 0, U32(1));
            var req = ReadPacket(s);
            Assert.Equal(U32(0), req.Payload);
            WritePacket(s, 0, 1, Sample(0));
        });

        using var client = OpenRgbClient.TryConnect(port: server.Port);
        Assert.NotNull(client);
        Assert.Equal(0u, client.ProtocolVersion);
        var c = Assert.Single(client.GetControllers());
        Assert.Equal("", c.Vendor);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ServerDisconnectTurnsIntoIOException()
    {
        using var server = new FakeServer();
        var serverTask = server.Run(s => ServeHandshake(s, "OverMount", serverVersion: 5)); // then closes

        using var client = OpenRgbClient.TryConnect(port: server.Port);
        Assert.NotNull(client);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Throws<IOException>(() => client.GetControllers());
        Assert.False(client.Connected);
        Assert.Throws<IOException>(() => client.SetCustomMode(0));
        Assert.Throws<IOException>(() => client.UpdateLeds(0, [new LampColor(1, 2, 3)]));
    }

    [Fact]
    public async Task OversizedPacketIsAProtocolError()
    {
        using var server = new FakeServer();
        var serverTask = server.Run(s =>
        {
            ServeHandshake(s, "OverMount", serverVersion: 5);
            ReadPacket(s);
            s.Write(new W().Bytes("ORGB"u8.ToArray()).U32(0).U32(0).U32(OpenRgbProtocol.MaxPayloadSize + 1u).ToArray());
            try { s.ReadByte(); } catch (IOException) { }
        });

        using var client = OpenRgbClient.TryConnect(port: server.Port);
        Assert.NotNull(client);
        Assert.Throws<IOException>(() => client.GetControllers());
        Assert.False(client.Connected);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void NothingListeningReturnsNull()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Assert.Null(OpenRgbClient.TryConnect(port: port, timeout: TimeSpan.FromSeconds(1)));
    }
}
