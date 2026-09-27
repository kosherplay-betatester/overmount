using System.Buffers.Binary;

namespace Darkmount.Sensors;

/// <summary>
/// The shared memory OverMount's own CPU sensor publishes. CPU temperature needs a kernel driver (PawnIO) and
/// administrator rights, so a small helper (<c>OverMountSensor.exe --sensor-helper</c>, a scheduled task running as
/// SYSTEM) reads it and the app just reads this block. Layout, little-endian:
/// magic "OMCS" (u32), version (u32), update time (u64 Environment.TickCount64, system-wide), CPU °C (f32), CPU W (f32);
/// NaN = not available.
/// </summary>
public static class CpuSensorLink
{
    public const string MappingName = @"Global\OverMountCpuSensor";
    public const int Size = 64;
    public const uint Version = 1;
    const uint Magic = 0x53434D4F; // "OMCS"

    /// <summary>A helper that stopped leaves its last values behind: older than this and they're ignored.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(6);

    public sealed record Reading(double? CpuTemp, double? CpuPower, long UpdatedTicks, uint HelperVersion);

    public static void Encode(Span<byte> blob, double? temp, double? power, long ticks)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(blob, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(blob[4..], Version);
        BinaryPrimitives.WriteInt64LittleEndian(blob[8..], ticks);
        BinaryPrimitives.WriteSingleLittleEndian(blob[16..], (float)(temp ?? double.NaN));
        BinaryPrimitives.WriteSingleLittleEndian(blob[20..], (float)(power ?? double.NaN));
    }

    /// <summary>The reading in the block if it is one and not older than <see cref="MaxAge"/> at <paramref name="nowTicks"/>.</summary>
    public static Reading? Decode(ReadOnlySpan<byte> blob, long nowTicks)
    {
        if (blob.Length < 24 || BinaryPrimitives.ReadUInt32LittleEndian(blob) != Magic) return null;
        long ticks = BinaryPrimitives.ReadInt64LittleEndian(blob[8..]);
        if (nowTicks - ticks > MaxAge.TotalMilliseconds || ticks - nowTicks > 1000) return null;
        static double? Value(float v, double min, double max) => float.IsFinite(v) && v > min && v < max ? v : null;
        return new Reading(Value(BinaryPrimitives.ReadSingleLittleEndian(blob[16..]), 0, 150),
            Value(BinaryPrimitives.ReadSingleLittleEndian(blob[20..]), 0.5, 1000), ticks, BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]));
    }

    /// <summary>The helper's latest values, or null when it isn't running.</summary>
    public static Reading? TryRead() => SharedMemory.TryRead(MappingName) is { } blob ? Decode(blob, Environment.TickCount64) : null;

    static readonly string[] TemperatureNames =
        ["Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Core (Tdie)", "Package", "Core Max", "Tdie", "Tctl"];

    static readonly string[] PowerNames = ["Package", "CPU Package", "Core (SVI2 TFN)"];

    /// <summary>
    /// CPU temperature and package watts from LibreHardwareMonitor's CPU sensors (Type is "Temperature" or "Power"):
    /// the package/Tctl sensor by name, else the hottest core. Zero means "not read" (no driver access). With several
    /// CPUs the hottest temperature and the sum of the watts. Pure, for tests.
    /// </summary>
    public static (double? Temp, double? Power) Pick(IEnumerable<(int Cpu, string Type, string Name, double? Value)> sensors)
    {
        var valid = sensors.Where(s => s.Value is > 0 && double.IsFinite(s.Value.Value)).ToList();
        double? temp = null, power = null;
        foreach (var cpu in valid.GroupBy(s => s.Cpu))
        {
            var temps = cpu.Where(s => s.Type == "Temperature").ToList();
            double? t = TemperatureNames.Select(n => temps.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).Value)
                .FirstOrDefault(v => v is not null) ?? (temps.Count > 0 ? temps.Max(s => s.Value) : null);
            if (t is > 0 and < 150) temp = Math.Max(temp ?? 0, t.Value);
            var powers = cpu.Where(s => s.Type == "Power").ToList();
            double? p = PowerNames.Select(n => powers.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).Value)
                .FirstOrDefault(v => v is not null);
            if (p is > 0.5 and < 1000) power = (power ?? 0) + p.Value;
        }
        return (temp, power);
    }
}
