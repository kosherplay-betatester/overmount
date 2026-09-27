using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace Darkmount.Sensors;

/// <summary>
/// One MSI Afterburner monitoring entry. <see cref="Value"/> is null when Afterburner has no data. <see cref="Gpu"/> is the
/// 0-based GPU the source belongs to and <see cref="SrcId"/> Afterburner's language-independent source id
/// (<see cref="MahmSource"/>; <see cref="MahmSource.Unknown"/> in layouts without it).
/// </summary>
public sealed record MahmEntry(string Name, string Units, double? Value, uint Gpu, uint SrcId = MahmSource.Unknown);

/// <summary>
/// Afterburner's fixed source ids (MAHMSharedMemory.h). Names differ with the GPU count and language — e.g. a single
/// GPU's board power is just "Power" and its VRAM "Memory usage" — so GPU values are matched by id first.
/// </summary>
public static class MahmSource
{
    public const uint GpuTemperature = 0x00, GpuUsage = 0x30, GpuMemoryUsage = 0x31, GpuPower = 0x61;
    public const uint Unknown = 0xFFFFFFFF;

    /// <summary>Ids below this belong to a GPU (temperatures, clocks, usages, voltages, frame rates, power).</summary>
    public const uint FirstNonGpu = 0x80;
}

/// <summary>Parsed content of Afterburner's "MAHMSharedMemory".</summary>
public sealed partial class MahmData
{
    private readonly Dictionary<string, MahmEntry> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the entries carry real source ids. A layout without ids (or a writer that leaves them zero) would make
    /// every entry look like GPU temperature (id 0), so ids count only when at least one is non-zero.
    /// </summary>
    private readonly bool _hasSourceIds;

    public MahmData(IReadOnlyList<MahmEntry> entries)
    {
        Entries = entries;
        foreach (var e in entries)
            _byName.TryAdd(e.Name, e);
        _hasSourceIds = entries.Any(e => e.SrcId is not 0 and not MahmSource.Unknown);
    }

    public IReadOnlyList<MahmEntry> Entries { get; }

    /// <summary>Entry by name (case-insensitive), or null.</summary>
    public MahmEntry? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Value of the named entry (case-insensitive), or null when missing or without data.</summary>
    public double? Value(string name) => Find(name)?.Value;

    public bool HasFpsLowEntries => Find(PointOneLow) is not null || Find(OneLow) is not null;

    private const string PointOneLow = "Framerate 0.1% Low";
    private const string OneLow = "Framerate 1% Low";

    /// <summary>
    /// GPU index to use for GPU values: <paramref name="gpuIndex"/> when non-zero, otherwise the index with the
    /// highest power reading, else the highest index with a temperature, else the highest index seen (0 if none).
    /// An unindexed "GPU xxx" entry counts as index 1.
    /// </summary>
    public int SelectGpu(int gpuIndex)
    {
        if (gpuIndex > 0) return gpuIndex;

        if (_hasSourceIds)
        {
            // By id: the GPU drawing the most power, else the last one with a temperature, else the first GPU seen.
            var gpuEntries = Entries.Where(e => e.SrcId < MahmSource.FirstNonGpu).ToList();
            if (gpuEntries.Count > 0)
            {
                var powered = gpuEntries.Where(e => e.SrcId == MahmSource.GpuPower && e.Value is not null).ToList();
                if (powered.Count > 0) return (int)powered.MaxBy(e => e.Value!.Value)!.Gpu + 1;
                var hot = gpuEntries.Where(e => e.SrcId == MahmSource.GpuTemperature && e.Value is not null).ToList();
                if (hot.Count > 0) return (int)hot.Max(e => e.Gpu) + 1;
                return (int)gpuEntries.Min(e => e.Gpu) + 1;
            }
        }

        var gpus = Entries
            .Select(e => (Parsed: ParseGpuName(e.Name), e.Value))
            .Where(x => x.Parsed is not null)
            .Select(x => (Index: x.Parsed!.Value.Index, Metric: x.Parsed.Value.Metric, x.Value))
            .ToList();
        if (gpus.Count == 0) return 0;

        var power = gpus.Where(g => g.Metric == "power" && g.Value is not null).ToList();
        if (power.Count > 0) return power.MaxBy(g => g.Value!.Value).Index;

        var temp = gpus.Where(g => g.Metric == "temperature" && g.Value is not null).ToList();
        if (temp.Count > 0) return temp.Max(g => g.Index);

        return gpus.Max(g => g.Index);
    }

    /// <summary>Maps the entries to a partial snapshot (FPS values are always filled; the hub decides if a game runs).</summary>
    public Snapshot ToSnapshot(SensorOptions options)
    {
        int gpu = SelectGpu(options.GpuIndex);
        double? pointOne = Value(PointOneLow);
        double? one = Value(OneLow);

        return new Snapshot
        {
            CpuTemp = Value("CPU temperature"),
            CpuLoad = Value("CPU usage"),
            CpuPower = Value("CPU power"),
            RamUsedMb = Value("RAM usage"),
            GpuTemp = GpuValue(gpu, MahmSource.GpuTemperature, "temperature"),
            GpuLoad = GpuValue(gpu, MahmSource.GpuUsage, "usage"),
            GpuPower = GpuValue(gpu, MahmSource.GpuPower, "power"),
            VramUsedMb = GpuValue(gpu, MahmSource.GpuMemoryUsage, "memory usage"),
            Fps = Value("Framerate"),
            FpsLow = one ?? pointOne, // 1 % low is the number players compare; 0.1 % only when it's all Afterburner offers
            FpsLowLabel = one is not null || pointOne is null ? "1% low" : "0.1% low",
        };
    }

    /// <summary>A GPU value by source id (1-based <paramref name="gpu"/>), falling back to the "GPU{n} metric" names.</summary>
    private double? GpuValue(int gpu, uint srcId, string metric)
    {
        if (gpu <= 0) return null;
        if (_hasSourceIds && Entries.FirstOrDefault(e => e.SrcId == srcId && e.Gpu == gpu - 1) is { Value: { } byId }) return byId;
        return Value($"GPU{gpu} {metric}") ?? (gpu == 1 ? Value($"GPU {metric}") : null);
    }

    internal static (int Index, string Metric)? ParseGpuName(string name)
    {
        var m = GpuNameRegex().Match(name);
        if (!m.Success) return null;
        int index = m.Groups[1].Length == 0 ? 1 : int.Parse(m.Groups[1].Value);
        return (index, m.Groups[2].Value.ToLowerInvariant());
    }

    [GeneratedRegex(@"^GPU(\d*)\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex GpuNameRegex();
}

/// <summary>Parser for MSI Afterburner's "MAHMSharedMemory" (v2.0 layout).</summary>
public static class MahmReader
{
    public const string MappingName = "MAHMSharedMemory";

    private const uint Signature = 0x4D41484D; // 'MAHM'
    private const int NameOffset = 0, NameLength = 260;
    private const int UnitsOffset = 260, UnitsLength = 260;
    private const int DataOffset = 1300;
    private const int GpuOffset = 1316;
    private const int SrcIdOffset = 1320;

    /// <summary>Parses the blob, or returns null when it is not a valid MAHM block.</summary>
    public static MahmData? Parse(byte[] blob)
    {
        try
        {
            if (blob.Length < 20) return null;
            var span = blob.AsSpan();
            if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Signature) return null;

            long headerSize = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
            long count = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
            long entrySize = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
            if (entrySize < DataOffset + 4 || count > 10_000) return null;

            var entries = new List<MahmEntry>((int)count);
            for (long i = 0; i < count; i++)
            {
                long o = headerSize + i * entrySize;
                if (o + entrySize > blob.Length) break;
                int off = (int)o;

                string name = SharedMemory.ReadString(span, off + NameOffset, NameLength);
                string units = SharedMemory.ReadString(span, off + UnitsOffset, UnitsLength);
                float raw = BinaryPrimitives.ReadSingleLittleEndian(span[(off + DataOffset)..]);
                double? value = float.IsNaN(raw) || float.IsInfinity(raw) || raw == float.MaxValue ? null : raw;
                uint gpu = entrySize >= GpuOffset + 4
                    ? BinaryPrimitives.ReadUInt32LittleEndian(span[(off + GpuOffset)..])
                    : 0;
                uint srcId = entrySize >= SrcIdOffset + 4
                    ? BinaryPrimitives.ReadUInt32LittleEndian(span[(off + SrcIdOffset)..])
                    : MahmSource.Unknown;
                entries.Add(new MahmEntry(name, units, value, gpu, srcId));
            }
            return new MahmData(entries);
        }
        catch
        {
            return null;
        }
    }
}
