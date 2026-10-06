namespace Darkmount.App;

static class Program
{
    /// <summary>Signalled by "OverMount --exit" to close the running instance cleanly (restoring the dock).</summary>
    public const string ExitEventName = @"Local\OverMount.Exit";

    [STAThread]
    static int Main(string[] args)
    {
        bool Has(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);
        // Background roles first: no window, no settings, no migration (they may run as SYSTEM or as an administrator).
        if (Has("--sensor-helper")) return Helper.CpuSensorHelper.Run();
        if (Has("--setup-cpu-sensor")) return Setup.CpuSensorSetup.RunElevatedSetup();
        if (Has("--remove-cpu-sensor")) return Setup.CpuSensorSetup.RunElevatedRemove();
        if (Has("--afterburner-autostart") && args.Length >= 2) return Setup.Companions.RunElevatedAfterburnerAutostart(args[^1]);
        if (Has("--setup-openrgb") && args.Length >= 2) return Setup.OpenRgbSetup.RunElevatedSetup(args[^1]);
        if (Has("--setup-extras")) return Setup.Extras.RunElevated(args);
        Run(args, Has);
        return 0;
    }

    static void Run(string[] args, Func<string, bool> Has)
    {
        if (Has("--exit"))
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var running)) using (running) running.Set();
            return;
        }

        Setup.LegacyMigration.OnStartup(); // "Darkmount Hub" → OverMount: before anything reads settings or writes logs
        Application.ThreadException += (_, e) => Log.Write($"UI error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Fatal: {e.ExceptionObject}");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException); // before any window exists

        // The single-file release is also its own installer: started from anywhere but the install folder it offers to install.
        if (Has("--uninstall") || Has("--install") || (!Setup.Installer.IsInstalledCopy && !Setup.Installer.IsDeveloperBuild
                                                        && !Has("--portable") && !Has("--autostart")))
        {
            InitialiseUi();
            if (Has("--uninstall")) { Setup.SetupForm.Uninstall(silent: Has("--silent")); return; }
            if (Has("--install") && Has("--silent")) { Setup.SetupForm.InstallSilently(); return; }
            if (Setup.SetupForm.Run() != Setup.SetupResult.RunPortable) return;
        }

        using var single = new Mutex(initiallyOwned: true, @"Local\OverMount", out bool first);
        if (!first)
        {
            MessageBox.Show("OverMount is already running (see the tray icon).", "OverMount",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Log.Prune();
        InitialiseUi();
        Application.Run(new TrayApp());
    }

    static bool _uiReady;

    static void InitialiseUi()
    {
        if (_uiReady) return;
        _uiReady = true;
        ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001 // dark mode for common controls is marked experimental
        Application.SetColorMode(SystemColorMode.Dark);
#pragma warning restore WFO5001
    }
}
