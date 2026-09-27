using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace Darkmount.App.Setup;

/// <summary>A free app OverMount reads extra sensors from.</summary>
/// <param name="UninstallKey">Its entry under Windows' installed-apps keys (where the installer records its folder).</param>
/// <param name="DefaultFolder">Where its installer puts it by default.</param>
public sealed record CompanionApp(string Name, string Purpose, string WingetId, string ProcessName, string ExeName,
    string UninstallKey, string DefaultFolder, bool Optional, string DownloadPage);

/// <summary>What <see cref="Companions.Check"/> found for one app.</summary>
public sealed record CompanionStatus(CompanionApp App, string? Exe, bool Running)
{
    public bool Installed => Exe is not null;
}

/// <summary>
/// MSI Afterburner, RivaTuner Statistics Server and HWiNFO: finds them, installs them through winget (Windows' package
/// manager; each installer shows Windows' own permission prompt), starts them, and sets up Afterburner's "start with
/// Windows" exactly like Afterburner itself does (a logon task "MSIAfterburner" running "/s" with highest rights, which
/// also brings RivaTuner up).
/// </summary>
public static class Companions
{
    static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public static readonly CompanionApp Afterburner = new("MSI Afterburner", "CPU temperature and CPU watts on the dock",
        "Guru3D.Afterburner", "MSIAfterburner", "MSIAfterburner.exe", "Afterburner",
        Path.Combine(ProgramFilesX86, "MSI Afterburner"), false, "https://www.msi.com/Landing/afterburner/graphics-cards");

    public static readonly CompanionApp Rtss = new("RivaTuner Statistics Server", "FPS and 1% lows while you game",
        "Guru3D.RTSS", "RTSS", "RTSS.exe", "RTSS",
        Path.Combine(ProgramFilesX86, "RivaTuner Statistics Server"), false, "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/");

    public static readonly CompanionApp HwInfo = new("HWiNFO", "Optional: extra-precise sensors (turn on Shared Memory Support)",
        "REALiX.HWiNFO", "HWiNFO64", "HWiNFO64.exe", "HWiNFO® 64_is1",
        Path.Combine(ProgramFiles, "HWiNFO64"), true, "https://www.hwinfo.com/download/");

    public static IReadOnlyList<CompanionApp> All { get; } = [Afterburner, Rtss, HwInfo];

    /// <summary>Afterburner's own autostart task (its Settings → "Start with Windows" uses the same name).</summary>
    public const string AfterburnerTask = "MSIAfterburner";

    /// <summary>The app's exe if installed (from its installed-apps entry, else the default folder), or null.</summary>
    public static string? FindExe(CompanionApp app)
    {
        foreach (var folder in RegisteredFolders(app).Append(app.DefaultFolder))
        {
            var exe = Path.Combine(folder, app.ExeName);
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    static IEnumerable<string> RegisteredFolders(CompanionApp app)
    {
        foreach (var (hive, path) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\"),
                     (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\"),
                     (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\"),
                 })
        {
            string? folder;
            try
            {
                using var key = hive.OpenSubKey(path + app.UninstallKey);
                folder = FolderFromEntry(key?.GetValue("InstallLocation") as string, key?.GetValue("DisplayIcon") as string);
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { folder = null; }
            if (folder is not null) yield return folder;
        }
    }

    /// <summary>
    /// The install folder from an installed-apps entry: InstallLocation, else the folder of DisplayIcon
    /// (<c>"C:\...\MSI Afterburner\uninstall.exe"</c> or <c>C:\...\app.exe,0</c>). Pure, for tests.
    /// </summary>
    internal static string? FolderFromEntry(string? installLocation, string? displayIcon)
    {
        if (!string.IsNullOrWhiteSpace(installLocation)) return installLocation.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var icon = displayIcon.Trim();
        int comma = icon.LastIndexOf(',');
        if (comma > 0 && int.TryParse(icon[(comma + 1)..], out _)) icon = icon[..comma];
        icon = icon.Trim().Trim('"');
        return icon.Length > 0 && Path.IsPathFullyQualified(icon) ? Path.GetDirectoryName(icon) : null;
    }

    public static bool IsRunning(CompanionApp app)
    {
        try
        {
            var procs = Process.GetProcessesByName(app.ProcessName);
            foreach (var p in procs) p.Dispose();
            return procs.Length > 0;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>Installed? Running? For each app, in <see cref="All"/> order. Takes a moment (disk, registry, process list).</summary>
    public static IReadOnlyList<CompanionStatus> Check() => [.. All.Select(a => new CompanionStatus(a, FindExe(a), IsRunning(a)))];

    /// <summary>
    /// Worth offering the assistant on its own: a required app is missing, or installed but neither running nor starting
    /// with Windows (at logon it may simply not be up yet). Pure, for tests.
    /// </summary>
    internal static bool NeedsAttention(IReadOnlyList<CompanionStatus> apps, bool afterburnerAutostarts) =>
        apps.Any(s => !s.App.Optional && (!s.Installed || (!s.Running && !afterburnerAutostarts)));

    /// <summary>True when Windows' package manager can be used.</summary>
    public static bool WingetAvailable() => RunQuiet("winget", "--version") == 0;

    /// <summary>winget arguments for a silent, non-interactive install of exactly this package.</summary>
    internal static string WingetInstallArgs(CompanionApp app) =>
        $"install --id {app.WingetId} --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";

    /// <summary>Installs through winget (the installer asks Windows for permission). True when the app is there afterwards.</summary>
    public static async Task<bool> InstallAsync(CompanionApp app, CancellationToken ct = default)
    {
        using var p = Process.Start(new ProcessStartInfo("winget", WingetInstallArgs(app))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("winget could not be started.");
        var output = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        _ = p.StandardError.ReadToEndAsync(CancellationToken.None); // both pipes drained, so winget never blocks on a full one
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            throw;
        }
        Log.Write($"winget install {app.WingetId}: exit {p.ExitCode}");
        if (p.ExitCode != 0) Log.Write((await output).Trim().Split('\n').LastOrDefault()?.Trim() ?? "");
        return FindExe(app) is not null; // winget's exit codes vary ("already installed"…): the file is what counts
    }

    /// <summary>
    /// Starts the app minimised. Afterburner starts through its logon task when there is one (no permission prompt, and
    /// it brings RivaTuner up itself); otherwise Windows asks for permission, as these tools need administrator rights.
    /// Throws <see cref="System.ComponentModel.Win32Exception"/> (1223) when the user declines the prompt.
    /// </summary>
    public static void Start(CompanionApp app)
    {
        if (app == Afterburner && AfterburnerStartsWithWindows() && RunQuiet("schtasks", $"/Run /TN \"{AfterburnerTask}\"") == 0) return;
        var exe = FindExe(app) ?? throw new FileNotFoundException($"{app.Name} is not installed.");
        Process.Start(new ProcessStartInfo(exe, app == Afterburner ? "/s" : "")
        {
            UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)!,
        })?.Dispose();
    }

    /// <summary>True when Afterburner starts with Windows (its own logon task exists).</summary>
    public static bool AfterburnerStartsWithWindows() => RunQuiet("schtasks", $"/Query /TN \"{AfterburnerTask}\"") == 0;

    /// <summary>Runs a console tool without a window; its exit code, or -1 when it can't run or takes over 15 s.</summary>
    static int RunQuiet(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p is null) return -1;
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch (InvalidOperationException) { } return -1; }
            return p.ExitCode;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return -1; }
    }

    /// <summary>
    /// Afterburner's own logon task, as its Settings → "Start with Windows" creates it: highest rights, minimised ("/s"),
    /// no time limit and also on battery. Pure, for tests.
    /// </summary>
    internal static string AfterburnerTaskXml(string exe, string userSid) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <URI>\{AfterburnerTask}</URI>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(userSid)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <StartWhenAvailable>true</StartWhenAvailable>
          </Settings>
          <Triggers>
            <LogonTrigger>
              <UserId>{SecurityElement.Escape(userSid)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(exe)}</Command>
              <Arguments>/s</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    /// <summary>
    /// Creates Afterburner's autostart task (one Windows permission prompt). True when it exists afterwards.
    /// Throws <see cref="System.ComponentModel.Win32Exception"/> (1223) when the user declines the prompt.
    /// </summary>
    public static async Task<bool> EnableAfterburnerAutostartAsync()
    {
        var exe = FindExe(Afterburner) ?? throw new FileNotFoundException("MSI Afterburner is not installed.");
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
                  ?? throw new InvalidOperationException("Your Windows account could not be identified.");
        var xml = Path.Combine(Path.GetTempPath(), $"overmount-afterburner-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(xml, AfterburnerTaskXml(exe, sid), System.Text.Encoding.Unicode);
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks", $"/Create /TN \"{AfterburnerTask}\" /XML \"{xml}\" /F")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is not null) await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        }
        finally
        {
            try { File.Delete(xml); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return AfterburnerStartsWithWindows();
    }
}
