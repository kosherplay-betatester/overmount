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
/// It runs for days, so a failed reading (a driver hiccup, or the PC running out of memory) is logged and retried rather
/// than ending the process; the startup task also starts it again every few minutes if it is gone.
/// </summary>
static class CpuSensorHelper
{
    const string SingleInstance = @"Global\OverMountCpuSensor.Helper";

    /// <summary>Reopen the sensor library after this many failed readings in a row.</summary>
    internal const int ReopenAfterFailures = 10;

    /// <summary>Repeated failures are logged at most this often, so a long outage can't fill the disk.</summary>
    internal const long LogEveryMs = 10 * 60 * 1000;

    public static int Run()
    {
        LogCrashes();
        using var single = new Mutex(true, SingleInstance, out bool first);
        if (!first) return 0;
        Log.Prune();

        var mapping = CreateMapping(out nint view);
        if (mapping == 0)
        {
            Log.Write("CPU sensor helper: the shared memory could not be created");
            return 3;
        }
        var blob = new byte[CpuSensorLink.Size];
        using var cpu = new CpuSource();
        try
        {
            Poll(cpu.Read, cpu.Reopen, (temp, power) =>
            {
                CpuSensorLink.Encode(blob, temp, power, Environment.TickCount64);
                Marshal.Copy(blob, 0, view, blob.Length);
            }, IsOverMountRunning, keepGoing: () => true);
            return 0;
        }
        finally
        {
            UnmapViewOfFile(view);
            CloseHandle(mapping);
        }
    }

    /// <summary>
    /// The polling loop: every <paramref name="periodMs"/> while OverMount runs (5 × that while it doesn't), read and
    /// publish. An exception in one round is logged (rate-limited) and the next round backs off a little, up to 30
    /// periods; after <see cref="ReopenAfterFailures"/> failures in a row the sensors are reopened. Nothing in a round
    /// can end the loop. Allocates nothing that outlives a round.
    /// </summary>
    internal static void Poll(Func<(double? Temp, double? Power)> read, Action reopen, Action<double?, double?> publish,
        Func<bool> overMountRunning, Func<bool> keepGoing, int periodMs = 1000)
    {
        int failures = 0;
        long lastLogged = long.MinValue / 2;
        while (keepGoing())
        {
            int sleep = periodMs * 5; // nothing to do until OverMount starts again
            try
            {
                if (overMountRunning())
                {
                    var (temp, power) = read();
                    publish(temp, power);
                    if (failures > 0 && Environment.TickCount64 - lastLogged >= LogEveryMs)
                    {
                        lastLogged = Environment.TickCount64; // same limit as failures, so a flapping sensor can't flood the log
                        SafeLog(() => $"CPU sensor helper: readings are back after {failures} failed attempts");
                    }
                    failures = 0;
                    sleep = periodMs;
                }
            }
            catch (Exception e)
            {
                failures++;
                long now = Environment.TickCount64;
                if (failures == 1 || now - lastLogged >= LogEveryMs)
                {
                    lastLogged = now;
                    SafeLog(() => $"CPU sensor helper: reading failed ({failures} in a row): {e}");
                }
                if (failures % ReopenAfterFailures == 0)
                {
                    try { reopen(); }
                    catch (Exception again) { SafeLog(() => $"CPU sensor helper: reopening the sensors failed: {again.Message}"); }
                }
                sleep = periodMs * Math.Min(failures, 30);
            }
            Thread.Sleep(sleep);
        }
    }

    /// <summary>Builds and writes a log line; never throws (even out of memory, building the text can fail).</summary>
    static void SafeLog(Func<string> message)
    {
        try { Log.Write(message()); }
        catch (Exception) { /* logging must never end the helper */ }
    }

    /// <summary>Whatever still escapes is written to the log before the process ends, so the next crash has a cause.</summary>
    static void LogCrashes()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            SafeLog(() => $"CPU sensor helper crashed{(e.IsTerminating ? "" : " (not terminating)")}: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            SafeLog(() => $"CPU sensor helper: unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
    }

    /// <summary>LibreHardwareMonitor's CPU sensors, opened on first use and reopened after repeated failures.</summary>
    sealed class CpuSource : IDisposable
    {
        Computer? _computer;

        public (double? Temp, double? Power) Read()
        {
            if (_computer is null)
            {
                var computer = new Computer { IsCpuEnabled = true };
                try { computer.Open(); }
                catch
                {
                    try { computer.Close(); } catch (Exception) { /* already failing: the open error is the one that matters */ }
                    throw; // whatever opened before the failure is released, so retries can't pile up driver handles
                }
                _computer = computer;
            }
            return CpuSensorHelper.Read(_computer);
        }

        public void Reopen()
        {
            var old = _computer;
            _computer = null; // opened again on the next read, even if closing this one fails
            old?.Close();
        }

        public void Dispose()
        {
            try { Reopen(); }
            catch (Exception e) { SafeLog(() => $"CPU sensor helper: closing the sensors failed: {e.Message}"); }
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
