using Microsoft.Win32;

namespace Darkmount.App.IoCenter;

/// <summary>
/// Windows' own startup-app switch (Task Manager → Startup apps): a value named like the Run entry under
/// <c>Explorer\StartupApproved\Run</c>. Its first byte is even (02, 06) when the app may start and odd (03, 07) when
/// the user turned it off; the next 8 bytes are when that happened. No value means enabled.
/// </summary>
public static class StartupApproval
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static bool IsDisabled(byte[]? value) => value is { Length: > 0 } && (value[0] & 1) == 1;

    /// <summary>The value Task Manager writes when you disable an app: 03 00 00 00 and the time as a FILETIME.</summary>
    public static byte[] DisabledValue(DateTime utc)
    {
        var value = new byte[12];
        value[0] = 3;
        BitConverter.GetBytes(utc.ToFileTimeUtc()).CopyTo(value, 4);
        return value;
    }

    public static byte[] EnabledValue() => [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public static bool IsDisabled(RegistryKey hive, string name)
    {
        using var key = hive.OpenSubKey(ApprovedRunKey);
        return IsDisabled(key?.GetValue(name) as byte[]);
    }

    /// <summary>Writes only when the switch is not already in that position.</summary>
    public static void SetEnabled(RegistryKey hive, string name, bool enabled)
    {
        if (IsDisabled(hive, name) != enabled) return;
        using var key = hive.CreateSubKey(ApprovedRunKey);
        key.SetValue(name, enabled ? EnabledValue() : DisabledValue(DateTime.UtcNow), RegistryValueKind.Binary);
    }
}

/// <summary>
/// Whether IO Center starts with Windows (its installer adds "IO Center" to the user's Run key) and turning that off
/// the same way Task Manager does, so it is easy to undo there. The Run entry itself is left alone.
/// </summary>
public sealed class IoCenterAutostart(RegistryKey hive)
{
    public static IoCenterAutostart CurrentUser { get; } = new(Registry.CurrentUser);

    /// <summary>Names of Run entries that start IO_Center.exe.</summary>
    public IReadOnlyList<string> Entries()
    {
        try
        {
            using var key = hive.OpenSubKey(StartupApproval.RunKey);
            if (key is null) return [];
            return [.. key.GetValueNames().Where(n => key.GetValue(n) is string cmd
                && cmd.Contains("IO_Center.exe", StringComparison.OrdinalIgnoreCase))];
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return []; }
    }

    /// <summary>True when an IO Center Run entry is present and not turned off in Task Manager.</summary>
    public bool IsEnabled()
    {
        try { return Entries().Any(n => !StartupApproval.IsDisabled(hive, n)); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>Turns IO Center's autostart off (Task Manager shows it as Disabled). Returns false if that failed.</summary>
    public bool Disable()
    {
        try
        {
            foreach (var name in Entries()) StartupApproval.SetEnabled(hive, name, false);
            return !IsEnabled();
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Turning off IO Center's autostart failed: {e.Message}");
            return false;
        }
    }
}
