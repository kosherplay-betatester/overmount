using System.Diagnostics;
using Microsoft.Win32;

namespace Darkmount.App.Setup;

/// <summary>
/// Per-user install (no admin rights): the single-file app copies itself to %LOCALAPPDATA%\Programs\OverMount, adds
/// Start-menu (and optionally desktop) shortcuts and an entry in Windows' installed apps, and can remove all of that again.
/// The same file is the setup program and the app, so updates simply run the newer file with <c>--install --silent</c>.
/// </summary>
public static class Installer
{
    public const string AppName = "OverMount";
    public const string ExeName = "OverMount.exe";
    public const string RepoUrl = "https://github.com/kosherplay-betatester/overmount";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OverMount";
    const string MutexName = @"Local\OverMount";

    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OverMount");

    public static string InstalledExe => Path.Combine(InstallDir, ExeName);

    static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    /// <summary>This program's version (major.minor.patch).</summary>
    public static Version CurrentVersion => Normalise(typeof(Installer).Assembly.GetName().Version);

    /// <summary>The installed copy's version, or null when not installed.</summary>
    public static Version? InstalledVersion
    {
        get
        {
            if (!File.Exists(InstalledExe)) return null;
            var info = FileVersionInfo.GetVersionInfo(InstalledExe);
            return Version.TryParse(info.ProductVersion?.Split('+')[0], out var v) || Version.TryParse(info.FileVersion, out v) ? Normalise(v) : new Version(0, 0, 0);
        }
    }

    /// <summary>Running from the install folder: start the app normally.</summary>
    public static bool IsInstalledCopy => SamePath(Environment.ProcessPath, InstalledExe);

    /// <summary>A build from the source tree (not the single-file release): never shows the setup window.</summary>
    public static bool IsDeveloperBuild => File.Exists(Path.Combine(AppContext.BaseDirectory, "OverMount.dll"));

    public static bool DesktopShortcutExists => File.Exists(DesktopShortcut);

    /// <summary>Drops a fourth component and turns missing parts into 0, so 1.2.0.0 equals "v1.2.0".</summary>
    public static Version Normalise(Version? v) => v is null ? new(0, 0, 0) : new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    public sealed record Options(bool StartWithWindows = true, bool DesktopShortcut = false, bool Launch = true);

    /// <summary>Installs (or updates) this copy. Closes a running OverMount first so it can restore the dock.</summary>
    public static void Install(Options options, IProgress<string>? progress = null)
    {
        progress?.Report("Closing OverMount if it is running…");
        CloseRunningInstance(TimeSpan.FromSeconds(20));
        LegacyMigration.OnInstall();

        Directory.CreateDirectory(InstallDir);
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find this program's file.");
        if (!SamePath(source, InstalledExe))
        {
            progress?.Report("Copying OverMount…");
            var staged = InstalledExe + ".new";
            File.Copy(source, staged, overwrite: true);
            for (int attempt = 1; ; attempt++)
            {
                try { File.Move(staged, InstalledExe, overwrite: true); break; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(500); } // the old copy may still be closing
            }
        }

        progress?.Report("Creating shortcuts…");
        Shortcut.Create(StartMenuShortcut, InstalledExe, "Control your be quiet! Dark Mount / Light Mount keyboard");
        if (options.DesktopShortcut) Shortcut.Create(DesktopShortcut, InstalledExe, "Control your be quiet! Dark Mount / Light Mount keyboard");
        else if (File.Exists(DesktopShortcut)) File.Delete(DesktopShortcut);

        progress?.Report("Registering with Windows…");
        RegisterUninstall();
        Autostart.Set(options.StartWithWindows, InstalledExe);
        RememberStartWithWindows(options.StartWithWindows);

        if (options.Launch)
        {
            progress?.Report("Starting OverMount…");
            Launch();
        }
        Log.Write($"Installed {CurrentVersion} to {InstallDir}");
    }

    /// <summary>Starts the installed copy.</summary>
    public static void Launch() =>
        Process.Start(new ProcessStartInfo(InstalledExe) { WorkingDirectory = InstallDir, UseShellExecute = false })?.Dispose();

    /// <summary>
    /// Removes shortcuts, the installed-apps entry and autostart, optionally the user's settings, and deletes the install
    /// folder once this process has exited.
    /// </summary>
    public static void Uninstall(bool removeUserData)
    {
        CloseRunningInstance(TimeSpan.FromSeconds(20));
        if (CpuSensorSetup.IsSetUp) CpuSensorSetup.Remove(); // the helper lives in Program Files: one permission prompt
        foreach (var link in new[] { StartMenuShortcut, DesktopShortcut })
            if (File.Exists(link)) File.Delete(link);
        Autostart.Set(false, InstalledExe);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        if (removeUserData)
        {
            // Settings, profiles, macros and backups (roaming) plus logs and cached display-key pictures (local).
            foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
            {
                var data = Path.Combine(Environment.GetFolderPath(root), "OverMount");
                if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
            }
        }
        if (Directory.Exists(InstallDir))
        {
            // The running exe can't delete itself: a hidden command prompt removes the folder a moment after we exit.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
            })?.Dispose();
        }
    }

    static void RegisterUninstall()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", CurrentVersion.ToString(3));
        key.SetValue("Publisher", "OverMount (community project)");
        key.SetValue("DisplayIcon", $"\"{InstalledExe}\",0");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --silent");
        key.SetValue("URLInfoAbout", RepoUrl);
        key.SetValue("HelpLink", RepoUrl + "#readme");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
    }

    /// <summary>The app re-applies its own "start with Windows" setting at startup, so the installer's choice is saved there too.</summary>
    static void RememberStartWithWindows(bool enabled)
    {
        try
        {
            var settings = SettingsStore.Load(SettingsStore.DefaultPath);
            if (settings.StartWithWindows == enabled) return;
            settings.StartWithWindows = enabled;
            SettingsStore.Save(SettingsStore.DefaultPath, settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"Saving the autostart choice failed: {e.Message}"); }
    }

    /// <summary>Asks a running OverMount to exit (it restores the dock and hands the lighting back) and waits for it.</summary>
    public static void CloseRunningInstance(TimeSpan timeout)
    {
        if (EventWaitHandle.TryOpenExisting(Program.ExitEventName, out var exit)) using (exit) exit.Set();
        var others = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)).Where(p => p.Id != Environment.ProcessId).ToList();
        var deadline = DateTime.UtcNow + timeout;
        foreach (var p in others)
        {
            using (p)
            {
                try
                {
                    var left = deadline - DateTime.UtcNow;
                    if (!p.WaitForExit(left > TimeSpan.Zero ? left : TimeSpan.Zero)) { p.Kill(); p.WaitForExit(5000); }
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        using var mutex = new Mutex(false, MutexName);
        try { if (mutex.WaitOne(TimeSpan.FromSeconds(5))) mutex.ReleaseMutex(); }
        catch (AbandonedMutexException) { mutex.ReleaseMutex(); }
    }
}
