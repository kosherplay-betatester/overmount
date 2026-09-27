using System.Diagnostics;
using System.Runtime.InteropServices;
using Darkmount.Sensors;
using LibreHardwareMonitor.Hardware;

namespace Darkmount.App.Helper;

/// <summary>
/// "--sensor-helper": OverMount's own CPU sensor. Runs as SYSTEM from a scheduled task (<see cref="Setup.CpuSensorSetup"/>)
/// because CPU temperature needs the PawnIO driver, which only administrators may use. Reads the CPU's temperature and
/// package watts with LibreHardwareMonitor once a second while OverMount runs, and publishes them in
/// <see cref="CpuSensorLink.MappingName"/> for the app. Only the CPU is opened (no fans, disks, controllers or network).
/// </summary>
static class CpuSensorHelper
{
    const string SingleInstance = @"Global\OverMountCpuSensor.Helper";

    public static int Run()
    {
        using var single = new Mutex(true, SingleInstance, out bool first);
        if (!first) return 0;

        var mapping = CreateMapping(out nint view);
        if (mapping == 0) return 3;
        Computer? computer = null;
        try
        {
            computer = new Computer { IsCpuEnabled = true };
            computer.Open();
            var blob = new byte[CpuSensorLink.Size];
            while (true)
            {
                if (IsOverMountRunning())
                {
                    var (temp, power) = Read(computer);
                    CpuSensorLink.Encode(blob, temp, power, Environment.TickCount64);
                    Marshal.Copy(blob, 0, view, blob.Length);
                    Thread.Sleep(1000);
                }
                else Thread.Sleep(5000); // nothing to do until OverMount starts again
            }
        }
        catch (Exception e)
        {
            Log.Write($"CPU sensor helper stopped: {e}");
            return 1;
        }
        finally
        {
            computer?.Close();
            UnmapViewOfFile(view);
            CloseHandle(mapping);
        }
    }

    static (double? Temp, double? Power) Read(Computer computer)
    {
        var sensors = new List<(int, string, string, double?)>();
        int index = 0;
        foreach (var hw in computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
        {
            hw.Update();
            foreach (var s in hw.Sensors.Where(s => s.SensorType is SensorType.Temperature or SensorType.Power))
                sensors.Add((index, s.SensorType.ToString(), s.Name, s.Value));
            index++;
        }
        return CpuSensorLink.Pick(sensors);
    }

    /// <summary>The tray app, in any session (the helper runs in session 0).</summary>
    static bool IsOverMountRunning()
    {
        try
        {
            var procs = Process.GetProcessesByName("OverMount");
            foreach (var p in procs) p.Dispose();
            return procs.Length > 0;
        }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>
    /// Global\ mapping readable by every signed-in user (the app runs without admin rights), writable only by SYSTEM
    /// and administrators.
    /// </summary>
    static nint CreateMapping(out nint view)
    {
        view = 0;
        const string Sddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;AU)S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, 1, out nint sd, 0)) return 0;
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };
            nint mapping = CreateFileMappingW(-1, ref sa, PageReadWrite, 0, CpuSensorLink.Size, CpuSensorLink.MappingName);
            if (mapping == 0) return 0;
            view = MapViewOfFile(mapping, FileMapWrite, 0, 0, CpuSensorLink.Size);
            if (view != 0) return mapping;
            CloseHandle(mapping);
            return 0;
        }
        finally { LocalFree(sd); }
    }

    const uint PageReadWrite = 0x04, FileMapWrite = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint sd, nint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateFileMappingW(nint file, ref SECURITY_ATTRIBUTES sa, uint protect, uint sizeHigh, uint sizeLow, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint MapViewOfFile(nint mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(nint view);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint mem);
}
