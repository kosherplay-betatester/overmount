using System.Buffers.Binary;
using System.Text;

namespace Darkmount.Keyboard.Lamps.OpenRgb;

/// <summary>OpenRGB device types, in the SDK's enum order (Laptop and Monitor exist from protocol 5).</summary>
public enum OpenRgbDeviceType
{
    Motherboard, Dram, Gpu, Cooler, LedStrip, Keyboard, Mouse, Mousemat, Headset, HeadsetStand, Gamepad, Light, Speaker,
    Virtual, Storage, Case, Microphone, Accessory, Keypad, Laptop, Monitor, Unknown,
}

/// <summary>
/// One controller mode. <see cref="RawBlock"/> holds the mode's exact bytes as the server sent them (same format UPDATEMODE
/// takes), so a mode can be restored byte-for-byte with <see cref="OpenRgbClient.UpdateMode"/>.
/// </summary>
public sealed record OpenRgbMode(string Name, uint Flags, uint ColorMode, byte[] RawBlock)
{
    /// <summary>MODE_FLAG_HAS_PER_LED_COLOR.</summary>
    public bool HasPerLedColor => (Flags & (1u << 5)) != 0;
}

/// <summary>
/// One zone. <see cref="StartLed"/> is the zone's first index in the controller's LED list. <see cref="Matrix"/> is
/// row-major (<see cref="MatrixHeight"/> × <see cref="MatrixWidth"/>), entries are zone-relative LED indices and
/// <see cref="OpenRgbProtocol.NoLed"/> marks an empty cell; it is empty when the zone has no matrix map.
/// Zone types: 0 single, 1 linear, 2 matrix.
/// </summary>
public sealed record OpenRgbZone(string Name, int Type, int StartLed, int LedCount, int MatrixHeight, int MatrixWidth, IReadOnlyList<uint> Matrix)
{
    public const int SingleType = 0, LinearType = 1, MatrixType = 2;
}

/// <summary>A parsed OpenRGB controller (REQUEST_CONTROLLER_DATA reply).</summary>
public sealed record OpenRgbController(int Index, OpenRgbDeviceType Type, string Name, string Vendor, string Description, string Serial, string Location,
    IReadOnlyList<OpenRgbMode> Modes, int ActiveMode, IReadOnlyList<OpenRgbZone> Zones, IReadOnlyList<string> LedNames)
{
    public int LedCount => LedNames.Count;

    /// <summary>Keyboards, keypads and laptops (the devices OverMount lights).</summary>
    public bool IsKeyboardLike => Type is OpenRgbDeviceType.Keyboard or OpenRgbDeviceType.Keypad or OpenRgbDeviceType.Laptop;
}

/// <summary>
/// OpenRGB SDK wire format: packet constants, payload encoders and the controller-data parser. Pure — no I/O.
/// All integers are little-endian; every packet starts with a 16-byte header "ORGB", u32 dev_id, u32 pkt_id, u32 pkt_size.
/// </summary>
public static class OpenRgbProtocol
{
    public const int DefaultPort = 6742;

    /// <summary>Highest protocol version this client speaks.</summary>
    public const uint ClientVersion = 5;

    public const int HeaderSize = 16;

    /// <summary>Largest payload the client accepts; anything bigger is treated as a protocol error.</summary>
    public const int MaxPayloadSize = 16 * 1024 * 1024;

    /// <summary>Matrix-map entry meaning "no LED in this cell".</summary>
    public const uint NoLed = 0xFFFFFFFF;

    public const uint RequestControllerCount = 0;
    public const uint RequestControllerData = 1;
    public const uint RequestProtocolVersion = 40;
    public const uint SetClientName = 50;
    public const uint DeviceListUpdated = 100;
    public const uint UpdateLeds = 1050;
    public const uint SetCustomMode = 1100;
    public const uint UpdateMode = 1101;

    static ReadOnlySpan<byte> Magic => "ORGB"u8;

    /// <summary>A complete packet: header + payload.</summary>
    public static byte[] Packet(uint devId, uint pktId, ReadOnlySpan<byte> payload = default)
    {
        var packet = new byte[HeaderSize + payload.Length];
        Magic.CopyTo(packet);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), devId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), pktId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    /// <summary>Decodes a header; false when the magic is wrong or the span is too short.</summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> header, out uint devId, out uint pktId, out uint size)
    {
        devId = pktId = size = 0;
        if (header.Length < HeaderSize || !header[..4].SequenceEqual(Magic)) return false;
        devId = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        pktId = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        size = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        return true;
    }

    /// <summary>A u32 payload (protocol version request, controller data request).</summary>
    public static byte[] UInt32Payload(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }

    /// <summary>SET_CLIENT_NAME payload: UTF-8 bytes plus a null terminator.</summary>
    public static byte[] ClientNamePayload(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name.Replace("\0", ""));
        Array.Resize(ref bytes, bytes.Length + 1);
        return bytes;
    }

    /// <summary>
    /// RGBColor u32 = R | G &lt;&lt; 8 | B &lt;&lt; 16 (top byte 0). OpenRGB has no intensity channel, so RGB is pre-scaled by
    /// I/255 when <see cref="LampColor.I"/> isn't 255 (same rule as LampArray devices without an intensity field).
    /// </summary>
    public static uint EncodeColor(LampColor c)
    {
        var (r, g, b) = c.I == 255 ? (c.R, c.G, c.B) : (Scale(c.R, c.I), Scale(c.G, c.I), Scale(c.B, c.I));
        return r | (uint)g << 8 | (uint)b << 16;
    }

    static byte Scale(byte value, byte intensity) => (byte)((value * intensity + 127) / 255);

    /// <summary>UPDATELEDS payload: u32 data_size (whole payload), u16 num_colors, num_colors × RGBColor.</summary>
    public static byte[] UpdateLedsPayload(IReadOnlyList<LampColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        if (colors.Count > ushort.MaxValue) throw new ArgumentException($"At most {ushort.MaxValue} colours per update.", nameof(colors));
        var payload = new byte[4 + 2 + 4 * colors.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), (ushort)colors.Count);
        for (var i = 0; i < colors.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6 + 4 * i), EncodeColor(colors[i]));
        return payload;
    }

    /// <summary>UPDATEMODE payload: u32 data_size (whole payload), i32 mode_idx, then the raw mode block.</summary>
    public static byte[] UpdateModePayload(int modeIndex, ReadOnlySpan<byte> rawModeBlock)
    {
        var payload = new byte[4 + 4 + rawModeBlock.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), modeIndex);
        rawModeBlock.CopyTo(payload.AsSpan(8));
        return payload;
    }

    /// <summary>
    /// Parses a REQUEST_CONTROLLER_DATA reply payload (starting with its u32 data_size) at protocol <paramref name="version"/>.
    /// Throws <see cref="FormatException"/> on malformed or truncated data.
    /// </summary>
    public static OpenRgbController ParseController(ReadOnlySpan<byte> data, int index, uint version)
    {
        try
        {
            return Parse(data, index, version);
        }
        catch (Exception e) when (e is not FormatException)
        {
            throw new FormatException($"Malformed OpenRGB controller data: {e.Message}", e);
        }
    }

    static OpenRgbController Parse(ReadOnlySpan<byte> data, int index, uint version)
    {
        var r = new Reader(data);
        var dataSize = r.U32();
        if (dataSize < 4 || dataSize > (uint)data.Length)
            throw new FormatException($"Controller data_size {dataSize} doesn't fit the {data.Length}-byte payload.");
        r = new Reader(data[..(int)dataSize]) { Pos = 4 };

        var type = MapType(r.I32(), version);
        var name = r.Str();
        var vendor = version >= 1 ? r.Str() : "";
        var description = r.Str();
        _ = r.Str(); // device version
        var serial = r.Str();
        var location = r.Str();

        var numModes = r.U16();
        var activeMode = r.I32();
        var modes = new OpenRgbMode[numModes];
        for (var i = 0; i < numModes; i++) modes[i] = ReadMode(ref r, data, version);

        var numZones = r.U16();
        var zones = new OpenRgbZone[numZones];
        var start = 0L;
        for (var i = 0; i < numZones; i++)
        {
            zones[i] = ReadZone(ref r, (int)Math.Min(start, int.MaxValue), version);
            start += zones[i].LedCount;
        }

        var numLeds = r.U16();
        var leds = new string[numLeds];
        for (var i = 0; i < numLeds; i++)
        {
            leds[i] = r.Str();
            _ = r.U32(); // value
        }

        var numColors = r.U16();
        r.Skip(4 * numColors);

        if (version >= 5)
        {
            var altNames = r.U16();
            for (var i = 0; i < altNames; i++) _ = r.Str();
            _ = r.U32(); // flags
        }

        return new OpenRgbController(index, type, name, vendor, description, serial, location, modes, activeMode, zones, leds);
    }

    static OpenRgbDeviceType MapType(int raw, uint version)
    {
        if (raw < 0 || raw >= (int)OpenRgbDeviceType.Unknown) return OpenRgbDeviceType.Unknown;
        if (version < 5 && raw >= (int)OpenRgbDeviceType.Laptop) return OpenRgbDeviceType.Unknown;
        return (OpenRgbDeviceType)raw;
    }

    static OpenRgbMode ReadMode(ref Reader r, ReadOnlySpan<byte> data, uint version)
    {
        var begin = r.Pos;
        var name = r.Str();
        _ = r.I32(); // value
        var flags = r.U32();
        r.Skip(8); // speed_min, speed_max
        if (version >= 3) r.Skip(8); // brightness_min, brightness_max
        r.Skip(12); // colors_min, colors_max, speed
        if (version >= 3) r.Skip(4); // brightness
        _ = r.U32(); // direction
        var colorMode = r.U32();
        var numColors = r.U16();
        r.Skip(4 * numColors);
        return new OpenRgbMode(name, flags, colorMode, data[begin..r.Pos].ToArray());
    }

    static OpenRgbZone ReadZone(ref Reader r, int startLed, uint version)
    {
        var name = r.Str();
        var type = r.I32();
        r.Skip(8); // leds_min, leds_max
        var count = r.U32();
        if (count > int.MaxValue) throw new FormatException($"Zone '{name}' has {count} LEDs.");

        var matrixLen = r.U16();
        int height = 0, width = 0;
        uint[] matrix = [];
        if (matrixLen > 0)
        {
            var matrixStart = r.Pos;
            var h = r.U32();
            var w = r.U32();
            if (h > matrixLen || w > matrixLen || 8 + 4L * h * w > matrixLen)
                throw new FormatException($"Zone '{name}' matrix {h}×{w} doesn't fit matrix_len {matrixLen}.");
            matrix = new uint[h * w];
            for (var i = 0; i < matrix.Length; i++) matrix[i] = r.U32();
            r.Pos = matrixStart;
            r.Skip(matrixLen);
            (height, width) = ((int)h, (int)w);
        }

        if (version >= 4)
        {
            var segments = r.U16();
            for (var i = 0; i < segments; i++)
            {
                _ = r.Str();
                r.Skip(12); // type, start_idx, leds_count
            }
        }
        if (version >= 5) _ = r.U32(); // zone_flags

        return new OpenRgbZone(name, type, startLed, (int)count, height, width, matrix);
    }

    /// <summary>Bounds-checked little-endian reader; every overrun is a <see cref="FormatException"/>.</summary>
    ref struct Reader(ReadOnlySpan<byte> data)
    {
        readonly ReadOnlySpan<byte> _data = data;
        public int Pos;

        ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || n > _data.Length - Pos)
                throw new FormatException($"Truncated OpenRGB data: need {n} bytes at offset {Pos}, have {_data.Length - Pos}.");
            var s = _data.Slice(Pos, n);
            Pos += n;
            return s;
        }

        public void Skip(int n) => Take(n);
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        /// <summary>u16 length (including the null terminator), then the bytes; decoded as UTF-8 with nulls trimmed.</summary>
        public string Str()
        {
            var len = U16();
            return Encoding.UTF8.GetString(Take(len)).Trim('\0');
        }
    }
}
