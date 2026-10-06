using System.Diagnostics;
using System.Net.Sockets;
using System.Security;
using System.Security.Principal;
using Darkmount.Keyboard.Lamps;

namespace Darkmount.App.Setup;

/// <summary>
/// OpenRGB (free, open source) lights hundreds of keyboards and laptops that don't support the Windows Dynamic Lighting
/// standard (most ASUS TUF/ROG, MSI, Razer, Corsair…). OverMount draws its scenes on them through OpenRGB's SDK server.
/// This finds hardware that probably needs it, installs it through winget and starts it with Windows from a logon task
/// with highest rights ("--server --startminimized"; it needs administrator rights for laptop and motherboard lighting),
/// all in one elevated run of this exe ("--setup-openrgb &lt;your account&gt;", one Windows permission prompt).
/// </summary>
public static class OpenRgbSetup
{
    static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public static readonly CompanionApp App = new("OpenRGB",
        "Lights keyboards and laptops without Windows Dynamic Lighting (ASUS, MSI, Razer, Corsair…)",
        "OpenRGB.OpenRGB", "OpenRGB", "OpenRGB.exe", "OpenRGB", Path.Combine(ProgramFiles, "OpenRGB"), true, "https://openrgb.org/");

    /// <summary>The logon task that starts OpenRGB's server with Windows.</summary>
    public const string TaskName = "OverMount OpenRGB";
    public const string ServerArgs = "--server --startminimized";

    /// <summary>
    /// The official OpenRGB 1.0 installer (the one winget installs) and its SHA-256, used when winget isn't available:
    /// the file is only run if it matches.
    /// </summary>
    internal const string MsiUrl = "https://github.com/CalcProgrammer1/OpenRGB/releases/download/release_1.0/OpenRGB_1.0_Windows_64_81bbe18.msi";
    internal const string MsiSha256 = "27cd65546ea321eb61403825af494bb5caf2a212bafdbd55de7a061b0c3ef553";

    public static bool Installed => Companions.FindExe(App) is not null;
    public static bool Running => Companions.IsRunning(App);
    public static bool StartsWithWindows => Companions.RunQuiet("schtasks", $"/Query /TN \"{TaskName}\"") == 0;

    /// <summary>OpenRGB's SDK server answers on this PC (a quick local connection; nothing is sent).</summary>
    public static bool ServerReachable(int port = 6742)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromMilliseconds(300)) && client.Connected;
        }
        catch (Exception e) when (e is SocketException or AggregateException or ObjectDisposedException) { return false; }
    }

    // ---------------------------------------------------------------- what needs it

    // Gaming laptops whose keyboard lighting OpenRGB supports (by maker and model name).
    static readonly (string Maker, string[] Models)[] Laptops =
    [
        ("ASUS", ["TUF", "ROG", "STRIX", "ZEPHYRUS", "SCAR", "FLOW"]),
        ("MICRO-STAR", ["GE", "GS", "GT", "GP", "RAIDER", "STEALTH", "VECTOR", "TITAN", "KATANA", "SWORD", "CROSSHAIR", "PULSE", "CYBORG"]),
        ("MSI", ["RAIDER", "STEALTH", "VECTOR", "TITAN", "KATANA", "SWORD", "CROSSHAIR", "PULSE", "CYBORG"]),
        ("LENOVO", ["LEGION", "LOQ"]),
        ("GIGABYTE", ["AORUS", "AERO"]),
        ("HP", ["OMEN", "VICTUS"]),
        ("ALIENWARE", [""]),
        ("DELL", ["ALIENWARE", "G15", "G16"]),
        ("ACER", ["PREDATOR", "NITRO", "HELIOS"]),
        ("RAZER", ["BLADE"]),
    ];

    /// <summary>
    /// Keyboard makers whose RGB keyboards OpenRGB supports (USB vendor ids). Logitech and HP aren't listed: their ids are
    /// mostly on keyboards without per-key lighting (or ones with Dynamic Lighting).
    /// </summary>
    static readonly Dictionary<int, string> RgbKeyboardMakers = new()
    {
        [0x0B05] = "ASUS", [0x1532] = "Razer", [0x1B1C] = "Corsair", [0x1038] = "SteelSeries", [0x0951] = "HyperX",
        [0x31E3] = "Wooting", [0x2516] = "Cooler Master", [0x1E7D] = "Roccat", [0x320F] = "Glorious", [0x1462] = "MSI",
    };

    /// <summary>A laptop name OpenRGB probably lights, from its maker and model ("ASUS TUF Gaming F15 FX506HM"); else null. Pure, for tests.</summary>
    internal static string? GamingLaptop(string? maker, string? model)
    {
        if (string.IsNullOrWhiteSpace(maker) || string.IsNullOrWhiteSpace(model)) return null;
        string m = maker.ToUpperInvariant(), name = model.Trim();
        var words = name.ToUpperInvariant().Split([' ', '-', '_', '(', ')', ','], StringSplitOptions.RemoveEmptyEntries);
        foreach (var (brand, models) in Laptops)
            if (m.Contains(brand) && models.Any(x => x.Length == 0 || words.Any(w => w.StartsWith(x, StringComparison.Ordinal))))
            {
                // A model word starting with the series name ("GE76", "Legion"), so "Prestige" isn't a "GE".
                string shown = m.Contains("ASUS") ? "ASUS" : maker.Trim();
                return name.StartsWith(shown, StringComparison.OrdinalIgnoreCase) ? name : $"{shown} {name}";
            }
        return null;
    }

    /// <summary>
    /// What on this PC probably needs OpenRGB to be lit ("ASUS TUF Gaming F15 FX506HM", "Razer keyboard"), or null.
    /// Reads the PC's maker/model (from the BIOS) and the keyboards' USB ids. Takes a moment: call it off the UI thread.
    /// </summary>
    public static string? FindHardware()
    {
        try
        {
            // Windows keeps the BIOS's maker and model here (the same values msinfo32 shows).
            using var bios = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var maker = bios?.GetValue("SystemManufacturer") as string;
            // Only on a laptop: desktop boards are named "ROG STRIX…" / "TUF GAMING…" too, and OpenRGB would light the board.
            bool battery = SystemInformation.PowerStatus.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery;
            if (battery)
                foreach (var model in new[] { bios?.GetValue("SystemFamily") as string, bios?.GetValue("SystemProductName") as string })
                    if (GamingLaptop(maker, model) is { } found) return found;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"OpenRGB check: PC model unreadable: {e.Message}");
        }
        const uint keyboardUsage = (0x01u << 16) | 0x06;
        bool laptop = SystemInformation.PowerStatus.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery;
        foreach (var device in HidSharp.DeviceList.Local.GetHidDevices())
        {
            if (!RgbKeyboardMakers.TryGetValue(device.VendorID, out var maker)) continue;
            try
            {
                var fields = HidReportDescriptor.Parse(device.GetRawReportDescriptor()).Fields;
                if (fields.Any(f => f.ApplicationUsage == keyboardUsage)) return laptop && maker == "ASUS" ? "ASUS laptop keyboard" : $"{maker} keyboard";
                // ASUS laptops whose model name isn't on the list: the keyboard's Aura lighting controller (page 0xFF31).
                if (laptop && device.VendorID == 0x0B05 && fields.Any(f => f.ApplicationUsage >> 16 == 0xFF31)) return "ASUS laptop keyboard";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or FormatException) { }
        }
        return null;
    }

    // ---------------------------------------------------------------- setting it up

    public enum Result { Running, Declined, Failed, NoWinget }

    /// <summary>
    /// Installs OpenRGB if needed, makes it start with Windows and starts it now: one elevated run of this exe (Windows
    /// asks for permission once), then waits up to 30 s for its server. Call it off the UI thread.
    /// </summary>
    public static async Task<Result> SetUpAsync()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        var exe = Environment.ProcessPath;
        if (sid is null || exe is null) return Result.Failed;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, $"--setup-openrgb {sid}")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null) return Result.Failed;
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(12));
            if (p.ExitCode != 0) { Log.Write($"OpenRGB setup exited with {p.ExitCode}"); return Result.Failed; }
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { return Result.Declined; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        {
            Log.Write($"OpenRGB setup failed: {e.Message}");
            return Result.Failed;
        }
        for (int i = 0; i < 60 && !ServerReachable(); i++) await Task.Delay(500); // OpenRGB scans its devices first
        return ServerReachable() ? Result.Running : Result.Failed;
    }

    /// <summary>Starts OpenRGB's server from its logon task (no permission prompt). False when there is no task.</summary>
    public static bool StartFromTask() => StartsWithWindows && Companions.RunQuiet("schtasks", $"/Run /TN \"{TaskName}\"") == 0;

    /// <summary>
    /// The logon task: OpenRGB's server for this user at sign-in, with highest rights, no time limit, also on battery.
    /// Pure, for tests.
    /// </summary>
    internal static string TaskXml(string exe, string userSid) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts OpenRGB's server so OverMount can light this PC's keyboard.</Description>
            <URI>\{SecurityElement.Escape(TaskName)}</URI>
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
              <Arguments>{ServerArgs}</Arguments>
              <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe)!)}</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;

    /// <summary>
    /// "--setup-openrgb &lt;sid&gt;", elevated: installs OpenRGB when missing (winget, else the official installer after
    /// checking its SHA-256), creates the logon task (only for an OpenRGB under Program Files, so nothing a normal program
    /// wrote runs with administrator rights) and starts it with its server — restarting an OpenRGB that runs without one.
    /// </summary>
    /// <summary>Downloads the official installer into Program Files\OverMount, checks its SHA-256 and installs it silently.</summary>
    static void InstallFromRelease()
    {
        var msi = CpuSensorSetup.ProtectedTempFile("openrgb.msi");
        try
        {
            Log.Write("winget unavailable or failed: downloading OpenRGB's installer");
            // The whole download is bounded (HttpClient.Timeout only covers the headers): a stalled connection can't hang setup.
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            using (var http = new System.Net.Http.HttpClient())
            using (var file = File.Create(msi))
            using (var body = http.GetStreamAsync(MsiUrl, cancel.Token).GetAwaiter().GetResult())
                body.CopyToAsync(file, cancel.Token).GetAwaiter().GetResult();
            string hash;
            using (var read = File.OpenRead(msi)) hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(read));
            if (!hash.Equals(MsiSha256, StringComparison.OrdinalIgnoreCase))
            {
                Log.Write($"OpenRGB installer has an unexpected SHA-256 ({hash}); not installing it");
                return;
            }
            int rc = Companions.RunQuiet("msiexec", $"/i \"{msi}\" /qn /norestart", TimeSpan.FromMinutes(10));
            Log.Write($"OpenRGB installer: exit {rc}");
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException
                                      or OperationCanceledException or InvalidOperationException)
        {
            Log.Write($"Downloading OpenRGB failed: {e.Message}");
        }
        finally
        {
            try { File.Delete(msi); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public static int RunElevatedSetup(string userSid)
    {
        try
        {
            var sid = new SecurityIdentifier(userSid).Value; // throws unless it's a real SID
            if (Companions.FindExe(App, trustedOnly: true) is null)
            {
                int rc = Companions.RunQuiet("winget", Companions.WingetInstallArgs(App), TimeSpan.FromMinutes(10));
                Log.Write($"winget install {App.WingetId}: exit {rc}");
            }
            if (Companions.FindExe(App, trustedOnly: true) is null) InstallFromRelease();
            var exe = Companions.FindExe(App, trustedOnly: true);
            if (exe is null) return 2;
            var xml = CpuSensorSetup.ProtectedTempFile("openrgb-task.xml");
            File.WriteAllText(xml, TaskXml(exe, sid), System.Text.Encoding.Unicode);
            try
            {
                if (Companions.RunQuiet("schtasks", $"/Create /TN \"{TaskName}\" /XML \"{xml}\" /F") != 0) return 3;
            }
            finally { File.Delete(xml); }
            // An OpenRGB that is just starting scans its devices before its server listens: give it time first.
            for (int i = 0; i < 30 && Companions.IsRunning(App) && !ServerReachable(); i++) Thread.Sleep(500);
            if (!ServerReachable())
            {
                // An OpenRGB open without its server (started by hand or by its own autostart) would make the task's start
                // a no-op: close it — only in this sign-in session, never another user's — so the task starts it with the server.
                int session = Process.GetCurrentProcess().SessionId;
                foreach (var p in Process.GetProcessesByName(App.ProcessName))
                    using (p)
                    {
                        try
                        {
                            if (p.SessionId != session) continue;
                            p.Kill();
                            p.WaitForExit(5000);
                        }
                        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                    }
                Companions.RunQuiet("schtasks", $"/Run /TN \"{TaskName}\"");
            }
            Log.Write("OpenRGB set up");
            return 0;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Log.Write($"OpenRGB setup failed: {e.Message}");
            return 1;
        }
    }
}
