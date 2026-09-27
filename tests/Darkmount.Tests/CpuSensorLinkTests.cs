using Darkmount.Sensors;

namespace Darkmount.Tests;

public class CpuSensorLinkTests
{
    [Fact]
    public void Readings_round_trip_and_go_stale()
    {
        var blob = new byte[CpuSensorLink.Size];
        CpuSensorLink.Encode(blob, 61.25, 87.5, ticks: 100_000);

        var r = CpuSensorLink.Decode(blob, nowTicks: 101_000)!;
        Assert.Equal(61.25, r.CpuTemp);
        Assert.Equal(87.5, r.CpuPower);
        Assert.Null(CpuSensorLink.Decode(blob, nowTicks: 100_000 + 7_000));   // the helper stopped: ignore old values
        Assert.Null(CpuSensorLink.Decode(new byte[CpuSensorLink.Size], 0));    // an empty block is no reading
    }

    [Fact]
    public void Missing_values_stay_missing()
    {
        var blob = new byte[CpuSensorLink.Size];
        CpuSensorLink.Encode(blob, null, 0.1, ticks: 5000); // 0.1 W is not a real package power
        var r = CpuSensorLink.Decode(blob, 5000)!;
        Assert.Null(r.CpuTemp);
        Assert.Null(r.CpuPower);
    }

    [Fact]
    public void Amd_and_intel_sensor_names_are_picked_and_unread_zeros_ignored()
    {
        // AMD Ryzen (LibreHardwareMonitor names): Tctl/Tdie and "Package" power; per-core temps must not win.
        var amd = CpuSensorLink.Pick([
            (0, "Temperature", "Core (Tctl/Tdie)", 68.4), (0, "Temperature", "CCD1 (Tdie)", 71.0),
            (0, "Power", "Package", 95.2), (0, "Power", "Core #1 (SMU)", 7.0)]);
        Assert.Equal((68.4, 95.2), amd);

        var intel = CpuSensorLink.Pick([(0, "Temperature", "Core #1", 55), (0, "Temperature", "CPU Package", 61), (0, "Power", "CPU Package", 44)]);
        Assert.Equal((61, 44), intel);

        // Without driver access LibreHardwareMonitor reports zeros: that's "unknown", not 0 °C.
        Assert.Equal((null, null), CpuSensorLink.Pick([(0, "Temperature", "Core (Tctl/Tdie)", 0), (0, "Power", "Package", 0)]));

        // No package sensor: the hottest core.
        Assert.Equal(77, CpuSensorLink.Pick([(0, "Temperature", "Core #1", 70), (0, "Temperature", "Core #2", 77)]).Temp);
    }

    [Fact]
    public void Cpu_sensor_task_runs_the_program_files_copy_as_system()
    {
        var xml = Darkmount.App.Setup.CpuSensorSetup.TaskXml(@"C:\Program Files\OverMount\OverMountSensor.exe");
        var task = System.Xml.Linq.XDocument.Parse(xml).Root!;
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("S-1-5-18", task.Descendants(ns + "UserId").Single().Value);
        Assert.EndsWith(@"\cmd.exe", task.Descendants(ns + "Command").Single().Value, StringComparison.OrdinalIgnoreCase);
        // The bundle unpacks into the admin-only Program Files folder, never a temp folder other accounts can write to.
        Assert.Equal("/d /c \"set \"DOTNET_BUNDLE_EXTRACT_BASE_DIR=C:\\Program Files\\OverMount\\runtime\"&& " +
                     "\"C:\\Program Files\\OverMount\\OverMountSensor.exe\" --sensor-helper\"", task.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("PT0S", task.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.Single(task.Descendants(ns + "BootTrigger"));
    }
}
