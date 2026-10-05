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
        // A helper that ended (e.g. crashed when the PC ran out of memory) is started again within 5 minutes;
        // a running one isn't doubled.
        Assert.Equal("PT5M", task.Descendants(ns + "TimeTrigger").Single().Descendants(ns + "Interval").Single().Value);
        Assert.Equal("IgnoreNew", task.Descendants(ns + "MultipleInstancesPolicy").Single().Value);
    }

    [Fact]
    public void Sensor_helper_survives_failed_readings_and_reopens_the_sensors()
    {
        Darkmount.App.Log.Enabled = false; // failures are logged: keep them out of the real log
        int round = 0, reopened = 0;
        var published = new List<double?>();
        (double?, double?) Read()
        {
            round++;
            if (round is >= 3 and <= 27) throw round % 2 == 0 ? new OutOfMemoryException() : new InvalidOperationException("driver hiccup");
            return (40 + round, 20);
        }

        Darkmount.App.Helper.CpuSensorHelper.Poll(Read, () => reopened++, (t, _) => published.Add(t),
            overMountRunning: () => true, keepGoing: () => round < 30, periodMs: 0);

        Assert.Equal(30, round);                       // kept going through 25 failures
        Assert.Equal(2, reopened);                     // after the 10th and 20th failure in a row
        Assert.Equal([41, 42, 68, 69, 70], published);
    }

    [Fact]
    public void An_older_sensor_copy_is_offered_an_update()
    {
        static bool Outdated(string? helper, string? app) => Darkmount.App.Setup.CpuSensorSetup.IsOutdated(
            helper is null ? null : Version.Parse(helper), app is null ? null : Version.Parse(app));

        Assert.True(Outdated("1.3.0.0", "1.5.1.0"));
        Assert.False(Outdated("1.5.1.0", "1.5.1.0"));
        Assert.False(Outdated("1.6.0.0", "1.5.1.0")); // never "update" to an older app
        Assert.False(Outdated(null, "1.5.1.0"));      // not set up: nothing to update
    }

    [Fact]
    public void Sensor_helper_does_not_read_while_overmount_is_closed()
    {
        Darkmount.App.Log.Enabled = false; // failures are logged: keep them out of the real log
        int checks = 0, reads = 0;
        Darkmount.App.Helper.CpuSensorHelper.Poll(() => { reads++; return (50, 10); }, () => { }, (_, _) => { },
            overMountRunning: () => false, keepGoing: () => ++checks <= 5, periodMs: 0);

        Assert.Equal(0, reads);
    }
}
