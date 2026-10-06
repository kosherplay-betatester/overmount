using System.Diagnostics;
using System.Security.Principal;

namespace Darkmount.App.Setup;

/// <summary>
/// The optional extras that need administrator rights — OpenRGB (lighting for keyboards and laptops without Windows
/// Dynamic Lighting) and OverMount's CPU sensor — set up together in one elevated run of OverMount
/// ("--setup-extras &lt;your account&gt; openrgb cpusensor"), so Windows asks for permission only once. Used by the
/// installer, so everything works from the first start.
/// </summary>
public static class Extras
{
    public const string OpenRgb = "openrgb", CpuSensor = "cpusensor";

    public enum Result { Done, Declined, Failed }

    /// <summary>
    /// Runs the chosen extras elevated (one permission prompt) with <paramref name="exe"/> (the installed copy, so the CPU
    /// sensor is copied from it). Blocks while Windows shows its prompt: call it off the UI thread.
    /// </summary>
    public static async Task<Result> SetUpAsync(string exe, bool openRgb, bool cpuSensor)
    {
        if (!openRgb && !cpuSensor) return Result.Done;
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null) return Result.Failed;
        string items = string.Join(" ", new[] { openRgb ? OpenRgb : null, cpuSensor ? CpuSensor : null }.OfType<string>());
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, $"--setup-extras {sid} {items}")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null) return Result.Failed;
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(15));
            Log.Write($"Extras ({items}) exited with {p.ExitCode}");
            return p.ExitCode == 0 ? Result.Done : Result.Failed;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { return Result.Declined; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        {
            Log.Write($"Setting up extras failed: {e.Message}");
            return Result.Failed;
        }
    }

    /// <summary>"--setup-extras &lt;sid&gt; [openrgb] [cpusensor]", elevated: each extra in turn; 0 when all worked.</summary>
    public static int RunElevated(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--setup-extras", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return 1;
        var items = args.Skip(i + 2).Select(a => a.ToLowerInvariant()).ToHashSet();
        int result = 0;
        if (items.Contains(OpenRgb))
        {
            int rc = OpenRgbSetup.RunElevatedSetup(args[i + 1]);
            if (rc != 0) result = rc;
        }
        if (items.Contains(CpuSensor))
        {
            int rc = CpuSensorSetup.RunElevatedSetup();
            if (rc != 0 && result == 0) result = 10 + rc;
        }
        return result;
    }
}
