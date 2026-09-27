using Darkmount.Sensors;

namespace Darkmount.Tests;

public class WindowsSensorsTests
{
    const string Dgpu = "luid_0x00000000_0x0000C6F3", Igpu = "luid_0x00000000_0x0000A001";

    [Fact]
    public void Luids_are_read_from_counter_instance_names()
    {
        Assert.Equal(0xC6F3L, WindowsSensors.ParseLuid($"pid_1234_{Dgpu}_phys_0_eng_0_engtype_3D"));
        Assert.Equal((0x1L << 32) | 0xFFFF_0001L, WindowsSensors.ParseLuid("luid_0x00000001_0xFFFF0001_phys_0"));
        Assert.Null(WindowsSensors.ParseLuid("_Total"));
    }

    [Fact]
    public void Load_is_the_busiest_engine_type_summed_over_processes_like_task_manager()
    {
        (string, double)[] engines =
        [
            ($"pid_100_{Dgpu}_phys_0_eng_0_engtype_3D", 40),
            ($"pid_200_{Dgpu}_phys_0_eng_0_engtype_3D", 35), // same engine type, another process → added
            ($"pid_100_{Dgpu}_phys_0_eng_5_engtype_VideoDecode", 20),
            ($"pid_300_{Igpu}_phys_0_eng_0_engtype_3D", 90),
        ];
        (string, double)[] memory = [($"{Dgpu}_phys_0", 6L * 1024 * 1024 * 1024), ($"{Igpu}_phys_0", 128L * 1024 * 1024)];

        var read = WindowsSensors.Combine(engines, memory)!.Value;

        Assert.Equal(0xC6F3L, read.Luid); // the card with its own memory, not the busy iGPU
        Assert.Equal(75, read.Load);
        Assert.Equal(6 * 1024, read.DedicatedMb);
    }

    [Fact]
    public void Load_is_capped_at_100_and_missing_counters_give_nothing()
    {
        var read = WindowsSensors.Combine([($"pid_1_{Dgpu}_eng_0_engtype_3D", 70), ($"pid_2_{Dgpu}_eng_0_engtype_3D", 60)], [])!.Value;
        Assert.Equal(100, read.Load);
        Assert.Null(read.DedicatedMb);
        Assert.Null(WindowsSensors.Combine([], []));
    }

    [Fact]
    public void Hub_fills_gaps_with_windows_values_but_afterburner_wins()
    {
        var windows = new Snapshot { CpuLoad = 11, GpuTemp = 50, GpuLoad = 12, GpuPower = 99, VramUsedMb = 1000, VramTotalMb = 16000 };
        using var hub = new SensorHub(new SensorOptions(), () => 32000, () => 8000, () => 12000, () => windows);
        var mahm = SensorBlobs.Mahm(SensorBlobs.M("GPU temperature", 64), SensorBlobs.M("CPU temperature", 71));

        var s = hub.Sample(null, mahm, null, 0, 1000);

        Assert.Equal(64, s.GpuTemp);    // Afterburner
        Assert.Equal(71, s.CpuTemp);    // Afterburner
        Assert.Equal(12, s.GpuLoad);    // Windows fills the gap
        Assert.Equal(99, s.GpuPower);
        Assert.Equal(11, s.CpuLoad);
        Assert.Equal(1000, s.VramUsedMb);
        Assert.Equal(16000, s.VramTotalMb); // the driver's total beats the registry estimate
    }
}
