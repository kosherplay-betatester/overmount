using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Darkmount.App;

/// <summary>System-wide hotkey via RegisterHotKey, e.g. "Ctrl+Alt+Shift+D".</summary>
public sealed class HotkeyWindow : NativeWindow, IDisposable
{
    const int WmHotkey = 0x0312;
    const int Id = 0xD0C;
    bool _registered;

    public event Action? Pressed;

    public HotkeyWindow() => CreateHandle(new CreateParams());

    /// <summary>Registers the hotkey; returns false if the text is invalid or the combination is taken.</summary>
    public bool Register(string hotkey)
    {
        Unregister();
        if (!TryParse(hotkey, out uint mods, out uint key)) return false;
        _registered = RegisterHotKey(Handle, Id, mods | 0x4000 /* MOD_NOREPEAT */, key);
        return _registered;
    }

    public static bool TryParse(string text, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= 0x2; break;
                case "alt": modifiers |= 0x1; break;
                case "shift": modifiers |= 0x4; break;
                case "win": modifiers |= 0x8; break;
                default:
                    if (!Enum.TryParse<Keys>(part, ignoreCase: true, out var k)) return false;
                    key = (uint)k;
                    break;
            }
        }
        return key != 0 && modifiers != 0;
    }

    void Unregister()
    {
        if (_registered) UnregisterHotKey(Handle, Id);
        _registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && (int)m.WParam == Id) Pressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>Start with Windows via HKCU\...\Run (and Task Manager's Startup apps switch, which can turn it off).</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "OverMount";

    static string Command(string? exe = null) => $"\"{exe ?? Environment.ProcessPath}\" --autostart";

    public static bool IsEnabled() => IsEnabledFor(null);

    /// <summary>True when Windows starts <paramref name="exe"/> (default: this program) at sign-in.</summary>
    public static bool IsEnabledFor(string? exe)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) is string v && string.Equals(v, Command(exe), StringComparison.OrdinalIgnoreCase)
               && !IoCenter.StartupApproval.IsDisabled(Registry.CurrentUser, Name);
    }

    /// <summary>The user turned OverMount off in Task Manager → Startup apps.</summary>
    public static bool IsDisabledInTaskManager()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) is not null && IoCenter.StartupApproval.IsDisabled(Registry.CurrentUser, Name);
    }

    /// <param name="exe">The program to start (default: this one; the installer passes the installed copy).</param>
    public static void Set(bool enabled, string? exe = null)
    {
        if (enabled && IsEnabledFor(exe)) return; // already so: don't rewrite the registry
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(Name, Command(exe));
            IoCenter.StartupApproval.SetEnabled(Registry.CurrentUser, Name, true); // undo a Task Manager "Disable"
        }
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
}
