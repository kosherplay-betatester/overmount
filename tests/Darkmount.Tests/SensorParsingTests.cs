using Darkmount.Sensors;
using Xunit.Abstractions;
using static Darkmount.Tests.SensorBlobs;

namespace Darkmount.Tests;

public class SensorParsingTests(ITestOutputHelper output)
{
    // ---------------- MAHM ----------------

    [Fact]
    public void Mahm_BadSignature_ReturnsNull()
    {
        var blob = Mahm(M("CPU usage", 5));
        blob[0] = 0;
        Assert.Null(MahmReader.Parse(blob));
        Assert.Null(MahmReader.Parse(new byte[8]));
    }

    [Fact]
    public void Mahm_ParsesEntries_AndFltMaxIsNull()
    {
        var blob = Mahm(
            M("CPU temperature", 55.5f, "°C"),
            M("Framerate", float.MaxValue, "FPS"),
            M("GPU2 usage", 40, "%", 1));
        var data = MahmReader.Parse(blob);
        Assert.NotNull(data);
        Assert.Equal(3, data.Entries.Count);
        Assert.Equal("CPU temperature", data.Entries[0].Name);
        Assert.Equal("°C", data.Entries[0].Units);
        Assert.Equal(55.5, data.Entries[0].Value);
        Assert.Null(data.Entries[1].Value);
        Assert.Equal(1u, data.Entries[2].Gpu);
        Assert.Equal(40, data.Value("gpu2 USAGE"));
    }

    [Fact]
    public void Mahm_ToSnapshot_MapsCpuRamAndGpu2Prefix()
    {
        var data = MahmReader.Parse(Mahm(
            M("CPU temperature", 61),
            M("CPU usage", 12),
            M("CPU power", 45),
            M("RAM usage", 12000),
            M("GPU2 temperature", 50),
            M("GPU2 usage", 97),
            M("GPU2 memory usage", 8000),
            M("GPU2 power", 250),
            M("Framerate", 144),
            M("Framerate 1% Low", 90)))!;
        var s = data.ToSnapshot(new SensorOptions());
        Assert.Equal(61, s.CpuTemp);
        Assert.Equal(12, s.CpuLoad);
        Assert.Equal(45, s.CpuPower);
        Assert.Equal(12000, s.RamUsedMb);
        Assert.Equal(50, s.GpuTemp);
        Assert.Equal(97, s.GpuLoad);
        Assert.Equal(8000, s.VramUsedMb);
        Assert.Equal(250, s.GpuPower);
        Assert.Equal(144, s.Fps);
        Assert.Equal(90, s.FpsLow);
        Assert.Equal("1% low", s.FpsLowLabel);
    }

    [Fact]
    public void Mahm_AutoGpu_PicksHighestPower()
    {
        var data = MahmReader.Parse(Mahm(
            M("GPU1 temperature", 40), M("GPU1 power", 300), M("GPU1 usage", 10),
            M("GPU2 temperature", 70), M("GPU2 power", 20), M("GPU2 usage", 90)))!;
        Assert.Equal(1, data.SelectGpu(0));
        var s = data.ToSnapshot(new SensorOptions());
        Assert.Equal(40, s.GpuTemp);
        Assert.Equal(300, s.GpuPower);

        var forced = data.ToSnapshot(new SensorOptions { GpuIndex = 2 });
        Assert.Equal(70, forced.GpuTemp);
        Assert.Equal(90, forced.GpuLoad);
    }

    [Fact]
    public void Mahm_AutoGpu_WithoutPower_PicksHighestIndexWithTemperature()
    {
        var data = MahmReader.Parse(Mahm(
            M("GPU1 temperature", 40), M("GPU2 temperature", 65), M("GPU3 usage", 5)))!;
        Assert.Equal(2, data.SelectGpu(0));
        Assert.Equal(65, data.ToSnapshot(new SensorOptions()).GpuTemp);
    }

    [Fact]
    public void Mahm_UnindexedGpu_CountsAsIndex1()
    {
        var data = MahmReader.Parse(Mahm(M("GPU temperature", 58), M("GPU usage", 33)))!;
        var s = data.ToSnapshot(new SensorOptions());
        Assert.Equal(58, s.GpuTemp);
        Assert.Equal(33, s.GpuLoad);
        Assert.Equal(1, data.SelectGpu(0));
    }

    [Fact]
    public void Mahm_single_gpu_power_and_vram_are_found_by_source_id() // GitHub issue #1
    {
        // Afterburner on one GPU only prefixes some sources with "GPU": power and VRAM come as "Power" / "Memory usage".
        var data = MahmReader.Parse(Mahm(
            M("GPU temperature", 61, "°C", gpu: 0, src: MahmSource.GpuTemperature),
            M("GPU usage", 97, "%", gpu: 0, src: MahmSource.GpuUsage),
            M("Memory usage", 9216, "MB", gpu: 0, src: MahmSource.GpuMemoryUsage),
            M("Power", 287.5f, "W", gpu: 0, src: MahmSource.GpuPower),
            M("CPU temperature", 70, "°C", src: 0x80),
            M("CPU usage", 35, "%", src: 0x90)))!;

        var s = data.ToSnapshot(new SensorOptions());

        Assert.Equal(61, s.GpuTemp);
        Assert.Equal(97, s.GpuLoad);
        Assert.Equal(287.5, s.GpuPower);
        Assert.Equal(9216, s.VramUsedMb);
        Assert.Equal(70, s.CpuTemp);
    }

    [Fact]
    public void Mahm_cpu_values_come_from_source_ids_or_per_core_entries_when_the_combined_ones_are_off()
    {
        // Only per-core graphs switched on in Afterburner (no source ids): hottest core, average usage.
        var perCore = MahmReader.Parse(Mahm(
            M("CPU1 temperature", 61), M("CPU2 temperature", 74), M("CPU1 usage", 20), M("CPU2 usage", 40),
            M("GPU temperature", 55, gpu: 0)))!.ToSnapshot(new SensorOptions());
        Assert.Equal(74, perCore.CpuTemp);
        Assert.Equal(30, perCore.CpuLoad);
        Assert.Null(perCore.CpuPower);

        // Unusual names but real source ids.
        var byId = MahmReader.Parse(Mahm(
            M("CPU package temperature", 68, src: MahmSource.CpuTemperature), M("CPU total usage", 42, src: MahmSource.CpuUsage),
            M("CPU package power", 95.5f, src: MahmSource.CpuPower), M("GPU temperature", 55, gpu: 0, src: MahmSource.GpuTemperature)))!
            .ToSnapshot(new SensorOptions());
        Assert.Equal(68, byId.CpuTemp);
        Assert.Equal(42, byId.CpuLoad);
        Assert.Equal(95.5, byId.CpuPower);
    }

    [Fact]
    public void Mahm_source_ids_pick_the_gpu_drawing_the_most_power()
    {
        var data = MahmReader.Parse(Mahm(
            M("GPU1 temperature", 45, gpu: 0, src: MahmSource.GpuTemperature),
            M("GPU1 power", 12, gpu: 0, src: MahmSource.GpuPower),
            M("GPU2 temperature", 66, gpu: 1, src: MahmSource.GpuTemperature),
            M("GPU2 power", 250, gpu: 1, src: MahmSource.GpuPower),
            M("GPU2 memory usage", 8000, gpu: 1, src: MahmSource.GpuMemoryUsage)))!;

        var s = data.ToSnapshot(new SensorOptions());

        Assert.Equal(2, data.SelectGpu(0));
        Assert.Equal(66, s.GpuTemp);
        Assert.Equal(250, s.GpuPower);
        Assert.Equal(8000, s.VramUsedMb);
        Assert.Equal(45, data.ToSnapshot(new SensorOptions { GpuIndex = 1 }).GpuTemp);
    }

    [Fact]
    public void Mahm_OnePercentLow_PreferredOverPointOnePercent()
    {
        var data = MahmReader.Parse(Mahm(
            M("Framerate", 120), M("Framerate 1% Low", 80), M("Framerate 0.1% Low", 60)))!;
        var s = data.ToSnapshot(new SensorOptions());
        Assert.Equal(80, s.FpsLow);
        Assert.Equal("1% low", s.FpsLowLabel);
    }

    [Fact]
    public void Mahm_PointOnePercentNoData_FallsBackToOnePercent()
    {
        var data = MahmReader.Parse(Mahm(
            M("Framerate", 120), M("Framerate 1% Low", 80), M("Framerate 0.1% Low", float.MaxValue)))!;
        var s = data.ToSnapshot(new SensorOptions());
        Assert.Equal(80, s.FpsLow);
        Assert.Equal("1% low", s.FpsLowLabel);
    }

    // ---------------- HWiNFO ----------------

    private static readonly string[] HwSensors =
        ["CPU [#0]: AMD Ryzen 7 9800X3D", "GPU [#0]: AMD Radeon(TM) Graphics", "GPU [#1]: NVIDIA GeForce RTX 5070 Ti", "System: Memory"];

    [Fact]
    public void HwInfo_DeadSignature_ReturnsNull()
    {
        var blob = HwInfo(HwSensors, [new(1, 0, "CPU Package", 50)], signature: 0x44414544);
        Assert.Null(HwInfoReader.Parse(blob, new SensorOptions()));
        Assert.Null(HwInfoReader.Parse(new byte[10], new SensorOptions()));
    }

    [Fact]
    public void HwInfo_MatchesLabels_ExactBeforeContains_AndUserLabels()
    {
        var blob = HwInfo(HwSensors,
        [
            new(1, 0, "CPU Package Something", 99),        // contains-match only
            new(1, 0, "CPU (Tctl/Tdie)", 71),              // exact match (second label) wins over contains
            new(5, 0, "Some Power", 88, LabelUser: "CPU Package Power"), // user label exact
            new(7, 0, "Total CPU Usage", 23),
            new(8, 3, "Physical Memory Used", 15000, Unit: "MB"),
        ]);
        var s = HwInfoReader.Parse(blob, new SensorOptions());
        Assert.NotNull(s);
        Assert.Equal(71, s.CpuTemp);
        Assert.Equal(88, s.CpuPower);
        Assert.Equal(23, s.CpuLoad);
        Assert.Equal(15000, s.RamUsedMb);
        Assert.Null(s.GpuTemp);
    }

    [Fact]
    public void HwInfo_ContainsMatch_WhenNoExact()
    {
        var blob = HwInfo(HwSensors, [new(1, 0, "CPU Package Temp", 66), new(5, 0, "CPU Package Power", 70)]);
        var s = HwInfoReader.Parse(blob, new SensorOptions())!;
        Assert.Equal(66, s.CpuTemp); // power reading is not a temperature
        Assert.Equal(70, s.CpuPower);
    }

    [Fact]
    public void HwInfo_GpuReadings_SkipIntegratedGpu()
    {
        var blob = HwInfo(HwSensors,
        [
            new(1, 1, "GPU Temperature", 45),
            new(5, 1, "GPU Power", 10),
            new(7, 1, "GPU Core Load", 3),
            new(1, 2, "GPU Temperature", 62),
            new(5, 2, "GPU Power", 280),
            new(7, 2, "GPU Core Load", 99),
            new(8, 2, "GPU Memory Allocated", 9000, Unit: "MB"),
        ]);
        var s = HwInfoReader.Parse(blob, new SensorOptions())!;
        Assert.Equal(62, s.GpuTemp);
        Assert.Equal(280, s.GpuPower);
        Assert.Equal(99, s.GpuLoad);
        Assert.Equal(9000, s.VramUsedMb);
    }

    [Fact]
    public void HwInfo_IntegratedGpuUsedWhenOnlyGpu()
    {
        var blob = HwInfo(HwSensors, [new(1, 1, "GPU Temperature", 45)]);
        Assert.Equal(45, HwInfoReader.Parse(blob, new SensorOptions())!.GpuTemp);
    }

    [Fact]
    public void HwInfo_CustomLabels()
    {
        var blob = HwInfo(HwSensors, [new(1, 0, "My CPU Temp", 77)]);
        var s = HwInfoReader.Parse(blob, new SensorOptions { CpuTempLabels = ["my cpu temp"] })!;
        Assert.Equal(77, s.CpuTemp);
    }

    // ---------------- RTSS ----------------

    [Fact]
    public void Rtss_BadSignature_ReturnsEmpty()
    {
        var blob = Rtss(new RtssEntryData(10, @"C:\g.exe", 0, 1000, 60, 16000));
        blob[0] = 0;
        Assert.Empty(RtssReader.Parse(blob, 1000));
        Assert.Empty(RtssReader.Parse(new byte[4], 1000));
    }

    [Fact]
    public void Rtss_ParsesFpsAndExeName_SkipsEmptySlots()
    {
        var blob = Rtss(
            null,
            new RtssEntryData(1234, @"C:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", 10_000, 10_500, 60, 8333),
            null,
            new RtssEntryData(42, "notepad.exe", 5000, 5000, 0, 0));
        var apps = RtssReader.Parse(blob, 11_000);
        Assert.Equal(2, apps.Count);
        var g = apps[0];
        Assert.Equal(1234, g.Pid);
        Assert.Equal("Cyberpunk2077.exe", g.ExeName);
        Assert.Equal(120, g.Fps, 3);
        Assert.Equal(8.333, g.FrameTimeMs, 3);
        Assert.Equal(500u, g.AgeMs);
        Assert.Equal("notepad.exe", apps[1].ExeName);
        Assert.Equal(0, apps[1].Fps);
    }

    [Fact]
    public void Rtss_AgeIsWrapSafe()
    {
        uint t1 = uint.MaxValue - 100;
        var blob = Rtss(new RtssEntryData(7, @"D:\x\game.exe", t1 - 1000, t1, 100, 10000));
        var app = Assert.Single(RtssReader.Parse(blob, 199));
        Assert.Equal(300u, app.AgeMs);
        Assert.Equal(100, app.Fps, 3);
    }

    // ---------------- SensorHub ----------------

    private static SensorHub TestHub(SensorOptions? o = null) =>
        new(o ?? new SensorOptions(), ramTotalMb: () => 32000, ramUsedMb: () => 10000, vramTotalMb: () => 16303);

    private static byte[] RtssGame(uint pid, string path, uint now, double fps = 100) =>
        Rtss(new RtssEntryData(pid, path, now - 1100, now - 100, (uint)Math.Round(fps), 10000));

    [Fact]
    public void Hub_Precedence_HwInfoThenMahmThenSystem()
    {
        using var hub = TestHub();
        var hw = HwInfo(HwSensors, [new(1, 0, "CPU Package", 70), new(7, 0, "Total CPU Usage", 30)]);
        var mahm = Mahm(M("CPU temperature", 60), M("CPU usage", 20), M("CPU power", 50), M("GPU2 temperature", 55));
        var s = hub.Sample(hw, mahm, null, 0, 100_000);
        Assert.Equal(70, s.CpuTemp);   // HWiNFO wins
        Assert.Equal(30, s.CpuLoad);
        Assert.Equal(50, s.CpuPower);  // MAHM fills gaps
        Assert.Equal(55, s.GpuTemp);
        Assert.Equal(10000, s.RamUsedMb); // SystemInfo fallback
        Assert.Equal(32000, s.RamTotalMb);
        Assert.Equal(16303, s.VramTotalMb);
        Assert.Null(s.GameName);
        Assert.Null(s.Fps);
    }

    [Fact]
    public void Hub_NoSources_AllHints()
    {
        using var hub = TestHub();
        var s = hub.Sample(null, null, null, 0, 1000);
        Assert.Null(s.CpuTemp);
        Assert.Equal(4, s.Hints.Count);
        Assert.Equal(SensorHub.HintCpuTemp, s.Hints[0]); // the most useful fix comes first (tray tooltip)
        Assert.Contains(s.Hints, h => h.Contains("HWiNFO"));
        Assert.Contains(s.Hints, h => h.Contains("Afterburner"));
        Assert.Contains(s.Hints, h => h.Contains("RivaTuner"));
    }

    [Fact]
    public void Hub_DetectsForegroundGame_UsesMahmFps()
    {
        using var hub = TestHub();
        const uint now = 50_000;
        var mahm = Mahm(M("Framerate", 141), M("Framerate 0.1% Low", 70));
        var s = hub.Sample(null, mahm, RtssGame(99, @"C:\G\Game.exe", now), 99, now);
        Assert.Equal("Game.exe", s.GameName);
        Assert.True(s.InGame);
        Assert.Equal(141, s.Fps);
        Assert.Equal(70, s.FpsLow);
        Assert.Equal("0.1% low", s.FpsLowLabel);
        Assert.Equal([SensorHub.HintCpuTemp, SensorHub.HintHwInfo], s.Hints); // no CPU temperature in this blob
    }

    [Fact]
    public void Hub_uses_overmounts_own_cpu_sensor_when_monitoring_apps_have_no_cpu_temperature()
    {
        var own = new CpuSensorLink.Reading(63.5, 88, 1000, CpuSensorLink.Version);
        using var hub = new SensorHub(new SensorOptions(), () => 32000, () => 8000, () => 12000, cpuSensor: () => own);

        var alone = hub.Sample(null, Mahm(M("GPU temperature", 55)), null, 0, 1000);
        Assert.Equal(63.5, alone.CpuTemp);
        Assert.Equal(88, alone.CpuPower);
        Assert.DoesNotContain(SensorHub.HintCpuTemp, alone.Hints);

        var withAfterburner = hub.Sample(null, Mahm(M("CPU temperature", 70), M("CPU power", 95)), null, 0, 1000);
        Assert.Equal(70, withAfterburner.CpuTemp); // Afterburner first, as configured by the user
        Assert.Equal(95, withAfterburner.CpuPower);
    }

    [Fact]
    public void Hub_GameFpsFallsBackToRtss_AndHintsMissingLow()
    {
        using var hub = TestHub();
        const uint now = 50_000;
        var mahm = Mahm(M("Framerate", float.MaxValue), M("CPU usage", 5));
        var s = hub.Sample(null, mahm, RtssGame(99, @"C:\G\Game.exe", now, 75), 99, now);
        Assert.Equal("Game.exe", s.GameName);
        Assert.Equal(75, s.Fps!.Value, 3);
        Assert.Null(s.FpsLow);
        Assert.Contains(s.Hints, h => h.Contains("1% low"));
    }

    [Fact]
    public void Hub_ExcludedOrStaleOrSlowApps_AreNotGames()
    {
        using var hub = TestHub();
        const uint now = 50_000;
        Assert.Null(hub.Sample(null, null, RtssGame(5, @"C:\x\CHROME.EXE", now), 5, now).GameName);
        var stale = Rtss(new RtssEntryData(6, @"C:\G\Game.exe", now - 5000, now - 3000, 100, 1));
        Assert.Null(hub.Sample(null, null, stale, 6, now).GameName);
        var slow = Rtss(new RtssEntryData(7, @"C:\G\Game.exe", now - 1100, now - 100, 0, 1));
        Assert.Null(hub.Sample(null, null, slow, 7, now).GameName);
        Assert.Null(hub.Sample(null, null, RtssGame(8, @"C:\G\Game.exe", now), 1, now).GameName); // not foreground
    }

    [Fact]
    public void Hub_AltTabDebounce_KeepsGameFor5Seconds()
    {
        using var hub = TestHub();
        uint now = 50_000;
        Assert.Equal("Game.exe", hub.Sample(null, null, RtssGame(99, @"C:\G\Game.exe", now), 99, now).GameName);
        now += 3000; // alt-tabbed to explorer (pid 1)
        var s = hub.Sample(null, null, RtssGame(99, @"C:\G\Game.exe", now), 1, now);
        Assert.Equal("Game.exe", s.GameName);
        Assert.NotNull(s.Fps);
        now += 3000; // more than 5 s since last foreground detection
        Assert.Null(hub.Sample(null, null, RtssGame(99, @"C:\G\Game.exe", now), 1, now).GameName);
    }

    [Fact]
    public void Options_JsonRoundTrip()
    {
        var o = new SensorOptions { GpuIndex = 2, CpuTempLabels = ["X"] };
        var json = System.Text.Json.JsonSerializer.Serialize(o);
        var back = System.Text.Json.JsonSerializer.Deserialize<SensorOptions>(json)!;
        Assert.Equal(2, back.GpuIndex);
        Assert.Equal(["X"], back.CpuTempLabels);
        Assert.Contains("dwm.exe", back.GameExcludes);
        Assert.Equal(2000, back.GameStaleMs);
    }

    // ---------------- Live (manual) ----------------

    [Fact(Skip = "Live hardware")]
    [Trait("Category", "Live")]
    public void Live_PrintSnapshot()
    {
        using var hub = new SensorHub(new SensorOptions());
        var s = hub.Sample();
        output.WriteLine(s.ToString());
        output.WriteLine($"Hints: {string.Join(" | ", s.Hints)}");
        output.WriteLine($"RAM total {SystemInfo.RamTotalMb}, VRAM total {SystemInfo.VramTotalMb}, fg pid {SystemInfo.ForegroundProcessId()}");
        var rtss = SharedMemory.TryRead("RTSSSharedMemoryV2");
        if (rtss is not null)
            foreach (var a in RtssReader.Parse(rtss, (uint)Environment.TickCount))
                output.WriteLine($"RTSS: {a}");
        var mahm = SharedMemory.TryRead("MAHMSharedMemory");
        if (mahm is not null && MahmReader.Parse(mahm) is { } d)
            foreach (var e in d.Entries)
                output.WriteLine($"MAHM: {e}");
    }
}
