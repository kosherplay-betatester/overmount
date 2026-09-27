using System.Buffers.Binary;
using System.Text;

namespace Darkmount.Tests;

/// <summary>Builds synthetic MAHM / HWiNFO / RTSS shared-memory blobs using the documented layouts.</summary>
internal static class SensorBlobs
{
    public static void U32(byte[] b, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off), v);
    public static void I64(byte[] b, int off, long v) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(off), v);
    public static void F32(byte[] b, int off, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(off), v);
    public static void F64(byte[] b, int off, double v) => BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(off), v);

    public static void Str(byte[] b, int off, string s) => Encoding.Latin1.GetBytes(s).CopyTo(b, off);

    // ---- MSI Afterburner (MAHM v2.0: header 32, entry 1324) ----

    public const int MahmHeader = 32;
    public const int MahmEntry = 1324;

    public static byte[] Mahm(params (string Name, string Units, float Value, uint Gpu, uint Src)[] entries)
    {
        var b = new byte[MahmHeader + entries.Length * MahmEntry];
        U32(b, 0, 0x4D41484D);
        U32(b, 4, 0x00020000);
        U32(b, 8, MahmHeader);
        U32(b, 12, (uint)entries.Length);
        U32(b, 16, MahmEntry);
        for (int i = 0; i < entries.Length; i++)
        {
            int o = MahmHeader + i * MahmEntry;
            Str(b, o, entries[i].Name);
            Str(b, o + 260, entries[i].Units);
            F32(b, o + 1300, entries[i].Value);
            U32(b, o + 1316, entries[i].Gpu);
            U32(b, o + 1320, entries[i].Src);
        }
        return b;
    }

    public static (string, string, float, uint, uint) M(string name, float value, string units = "", uint gpu = 0, uint src = 0) =>
        (name, units, value, gpu, src);

    // ---- HWiNFO (HWiNFO_SENS_SM2) ----

    public const int HwHeader = 44;
    public const int HwSensorSize = 264;
    public const int HwReadingSize = 316;

    public sealed record HwReading(uint Type, uint Sensor, string LabelOrig, double Value, string LabelUser = "", string Unit = "");

    public static byte[] HwInfo(string[] sensorNames, HwReading[] readings, uint signature = 0x53695748)
    {
        int sensorOff = HwHeader;
        int readingOff = sensorOff + sensorNames.Length * HwSensorSize;
        var b = new byte[readingOff + readings.Length * HwReadingSize];
        U32(b, 0, signature);
        U32(b, 4, 2);
        U32(b, 8, 1);
        I64(b, 12, 0);
        U32(b, 20, (uint)sensorOff);
        U32(b, 24, HwSensorSize);
        U32(b, 28, (uint)sensorNames.Length);
        U32(b, 32, (uint)readingOff);
        U32(b, 36, HwReadingSize);
        U32(b, 40, (uint)readings.Length);
        for (int i = 0; i < sensorNames.Length; i++)
        {
            int o = sensorOff + i * HwSensorSize;
            U32(b, o, (uint)(0xF0000000 + i));
            Str(b, o + 8, sensorNames[i]);
            Str(b, o + 136, sensorNames[i]);
        }
        for (int i = 0; i < readings.Length; i++)
        {
            var r = readings[i];
            int o = readingOff + i * HwReadingSize;
            U32(b, o, r.Type);
            U32(b, o + 4, r.Sensor);
            U32(b, o + 8, (uint)i);
            Str(b, o + 12, r.LabelOrig);
            Str(b, o + 140, r.LabelUser.Length > 0 ? r.LabelUser : r.LabelOrig);
            Str(b, o + 268, r.Unit);
            F64(b, o + 284, r.Value);
        }
        return b;
    }

    // ---- RivaTuner (RTSSSharedMemoryV2) ----

    public const int RtssHeader = 64;
    public const int RtssEntry = 12416;

    public sealed record RtssEntryData(uint Pid, string Path, uint Time0, uint Time1, uint Frames, uint FrameTimeUs);

    /// <summary>Entries are placed in consecutive slots; a null entry leaves an empty slot (pid 0).</summary>
    public static byte[] Rtss(params RtssEntryData?[] entries)
    {
        var b = new byte[RtssHeader + entries.Length * RtssEntry];
        U32(b, 0, 0x52545353);
        U32(b, 4, 0x00020015);
        U32(b, 8, RtssEntry);
        U32(b, 12, RtssHeader);
        U32(b, 16, (uint)entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (e is null) continue;
            int o = RtssHeader + i * RtssEntry;
            U32(b, o, e.Pid);
            Str(b, o + 4, e.Path);
            U32(b, o + 268, e.Time0);
            U32(b, o + 272, e.Time1);
            U32(b, o + 276, e.Frames);
            U32(b, o + 280, e.FrameTimeUs);
        }
        return b;
    }
}
