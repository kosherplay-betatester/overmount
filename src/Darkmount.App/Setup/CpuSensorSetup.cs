using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using Darkmount.Sensors;
using Microsoft.Win32;

namespace Darkmount.App.Setup;

/// <summary>
/// OverMount's own CPU sensor: CPU temperature (and watts) without MSI Afterburner or HWiNFO. Windows can't read CPU
/// temperature without a kernel driver, so this installs PawnIO (the open-source, signed driver LibreHardwareMonitor uses)
/// and runs a copy of OverMount as SYSTEM from a startup task ("--sensor-helper", <see cref="Helper.CpuSensorHelper"/>).
/// The copy lives in Program Files, which only administrators can change: a SYSTEM task must never run a file from the
/// user's own folders, where any program could replace it. Setup and removal each take one Windows permission prompt.
/// </summary>
public static class CpuSensorSetup
{
    public const string TaskName = "OverMount CPU sensor";
    const string PawnIoWingetId = "namazso.PawnIO";
    public const string PawnIoPage = "https://pawnio.eu/";
    const int Declined = 1223;

    static string HelperDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OverMount");
    public static string HelperExe => Path.Combine(HelperDir, "OverMountSensor.exe");

    /// <summary>
    /// Where the helper's single-file bundle unpacks its native libraries. By default that's the temp folder, which for
    /// SYSTEM can be C:\Windows\Temp, where any account on the PC may create files: someone could plant a library there
    /// and have SYSTEM load it. This folder, like the rest of Program Files, only administrators can change.
    /// </summary>
    static string ExtractDir => Path.Combine(HelperDir, "runtime");

    /// <summary>The helper is publishing fresh values.</summary>
    public static bool IsRunning => CpuSensorLink.TryRead() is not null;

    public static bool IsSetUp => File.Exists(HelperExe);

    public static bool PawnIoInstalled
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
                return key is not null;
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }
    }

    // ---------------------------------------------------------------- from the app (no admin rights)

    public enum Result { Running, Declined, NeedsPawnIo, Failed }

    /// <summary>
    /// Sets everything up in one elevated run of this exe ("--setup-cpu-sensor") and waits for the first reading.
    /// Blocks while Windows shows its permission prompt, so call it off the UI thread.
    /// </summary>
    public static async Task<Result> SetUpAsync()
    {
        if (!PawnIoInstalled && !Companions.WingetAvailable()) return Result.NeedsPawnIo;
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("OverMount's own file could not be found.");
        int exit;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "--setup-cpu-sensor")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null) return Result.Failed;
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(12));
            exit = p.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == Declined) { return Result.Declined; }
        catch (Exception e) when (e is Win32Exception or TimeoutException or InvalidOperationException)
        {
            Log.Write($"CPU sensor setup failed: {e.Message}");
            return Result.Failed;
        }
        if (exit == ExitNeedsPawnIo) return Result.NeedsPawnIo;
        if (exit != 0) { Log.Write($"CPU sensor setup exited with {exit}"); return Result.Failed; }
        for (int i = 0; i < 40 && !IsRunning; i++) await Task.Delay(500); // the helper's first reading
        return IsRunning ? Result.Running : Result.Failed;
    }

    /// <summary>Removes the task and the helper copy (one permission prompt). False when declined or it failed.</summary>
    public static bool Remove()
    {
        if (!IsSetUp && !TaskExists()) return true;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--remove-cpu-sensor")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            });
            p?.WaitForExit(60_000);
            return p is { HasExited: true, ExitCode: 0 };
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Write($"Removing the CPU sensor failed: {e.Message}");
            return false;
        }
    }

    static bool TaskExists() => Companions.RunQuiet("schtasks", $"/Query /TN \"{TaskName}\"") == 0;

    // ---------------------------------------------------------------- elevated ("--setup-cpu-sensor" / "--remove-cpu-sensor")

    const int ExitNeedsPawnIo = 2, ExitTaskFailed = 3, ExitFailed = 1;

    public static int RunElevatedSetup()
    {
        try
        {
            Log.Write("Setting up the CPU sensor");
            if (!PawnIoInstalled)
            {
                int rc = Companions.RunQuiet("winget", $"install --id {PawnIoWingetId} --exact --silent --accept-package-agreements " +
                                                       "--accept-source-agreements --disable-interactivity", TimeSpan.FromMinutes(10));
                Log.Write($"winget install {PawnIoWingetId}: exit {rc}");
                if (!PawnIoInstalled) return ExitNeedsPawnIo;
            }
            StopHelper();
            Directory.CreateDirectory(HelperDir);
            if (Directory.Exists(ExtractDir)) Directory.Delete(ExtractDir, recursive: true); // unpacked again for this version
            Directory.CreateDirectory(ExtractDir);
            // The file that asked for this is the one that runs as SYSTEM. The release isn't code-signed, so there's no
            // signature to check; Windows' permission prompt (for this very file) is the consent.
            File.Copy(Environment.ProcessPath!, HelperExe, overwrite: true);

            var xml = ProtectedTempFile("cpu-sensor-task.xml");
            File.WriteAllText(xml, TaskXml(HelperExe), System.Text.Encoding.Unicode);
            try
            {
                if (Companions.RunQuiet("schtasks", $"/Create /TN \"{TaskName}\" /XML \"{xml}\" /F") != 0) return ExitTaskFailed;
            }
            finally { File.Delete(xml); }
            Companions.RunQuiet("schtasks", $"/Run /TN \"{TaskName}\"");
            Log.Write("CPU sensor set up");
            return 0;
        }
        catch (Exception e)
        {
            Log.Write($"CPU sensor setup failed: {e}");
            return ExitFailed;
        }
    }

    public static int RunElevatedRemove()
    {
        try
        {
            StopHelper();
            Companions.RunQuiet("schtasks", $"/Delete /TN \"{TaskName}\" /F");
            if (Directory.Exists(HelperDir)) Directory.Delete(HelperDir, recursive: true);
            return 0;
        }
        catch (Exception e)
        {
            Log.Write($"Removing the CPU sensor failed: {e}");
            return ExitFailed;
        }
    }

    /// <summary>
    /// A fresh file name in Program Files\OverMount (elevated callers only). Task definitions go here, not to %TEMP%:
    /// a program running as the user could otherwise swap the file between writing it and schtasks reading it.
    /// </summary>
    internal static string ProtectedTempFile(string name)
    {
        Directory.CreateDirectory(HelperDir);
        return Path.Combine(HelperDir, $"{Guid.NewGuid():N}-{name}");
    }

    static void StopHelper()
    {
        Companions.RunQuiet("schtasks", $"/End /TN \"{TaskName}\"");
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(HelperExe)))
        {
            using (p)
            {
                try { p.Kill(); p.WaitForExit(5000); }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
            }
        }
    }

    /// <summary>
    /// The startup task: runs the helper as SYSTEM from boot (it only polls the CPU while OverMount runs), restarts it if
    /// it fails, never stops it for running long or on battery. Pure, for tests.
    /// </summary>
    internal static string TaskXml(string helperExe) => TaskXml(helperExe, Path.Combine(Path.GetDirectoryName(helperExe)!, "runtime"));

    /// <summary>
    /// Runs through cmd.exe only to set DOTNET_BUNDLE_EXTRACT_BASE_DIR (a task can't set environment variables): the
    /// bundle then unpacks into <paramref name="extractDir"/> instead of a temp folder other accounts can write to.
    /// </summary>
    internal static string TaskXml(string helperExe, string extractDir) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Reads the CPU temperature for OverMount (be quiet! Dark Mount dashboard).</Description>
            <URI>\{SecurityElement.Escape(TaskName)}</URI>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-18</UserId>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <StartWhenAvailable>true</StartWhenAvailable>
            <Hidden>true</Hidden>
            <RestartOnFailure>
              <Interval>PT1M</Interval>
              <Count>999</Count>
            </RestartOnFailure>
          </Settings>
          <Triggers>
            <BootTrigger />
          </Triggers>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(Path.Combine(Environment.SystemDirectory, "cmd.exe"))}</Command>
              <Arguments>{SecurityElement.Escape($"/d /c \"set \"DOTNET_BUNDLE_EXTRACT_BASE_DIR={extractDir}\"&& \"{helperExe}\" --sensor-helper\"")}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;
}
