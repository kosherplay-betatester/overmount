using System.Runtime.InteropServices;

namespace Darkmount.App.Macros;

/// <summary>RegisterHotKey/UnregisterHotKey, abstracted for tests. Real implementation: <see cref="Win32HotkeyApi"/>.</summary>
public interface IHotkeyApi
{
    /// <returns>False with the Win32 <paramref name="error"/> (1409 = combination already registered elsewhere).</returns>
    bool Register(IntPtr hwnd, int id, uint modifiers, uint vk, out int error);

    void Unregister(IntPtr hwnd, int id);
}

public sealed class Win32HotkeyApi : IHotkeyApi
{
    public bool Register(IntPtr hwnd, int id, uint modifiers, uint vk, out int error)
    {
        bool ok = RegisterHotKey(hwnd, id, modifiers, vk);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    public void Unregister(IntPtr hwnd, int id) => UnregisterHotKey(hwnd, id);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>A macro whose trigger could not be registered, with a user-facing reason.</summary>
public sealed record HotkeyFailure(Macro Macro, string Reason);

/// <summary>
/// Catches macro triggers system-wide: one RegisterHotKey (with MOD_NOREPEAT) per enabled macro on a hidden window.
/// Create, <see cref="Update"/> and dispose on the UI thread (hotkeys belong to the window's thread);
/// <see cref="Triggered"/> is raised on that thread.
/// </summary>
public sealed class MacroHotkeys : NativeWindow, IDisposable
{
    const int WmHotkey = 0x0312;
    const int FirstId = 0x1000;
    const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    const int ErrorHotkeyAlreadyRegistered = 1409;

    readonly IHotkeyApi _api;
    readonly Dictionary<int, Macro> _registered = [];
    IReadOnlyList<HotkeyFailure> _failures = [];

    public MacroHotkeys(IHotkeyApi? api = null)
    {
        _api = api ?? new Win32HotkeyApi();
        CreateHandle(new CreateParams());
    }

    public event Action<Macro>? Triggered;

    /// <summary>Failures of the last <see cref="Update"/>.</summary>
    public IReadOnlyList<HotkeyFailure> Failures => _failures;

    public int RegisteredCount => _registered.Count;

    /// <summary>
    /// Replaces all registrations with the enabled macros' triggers. A trigger used by two macros is registered for
    /// the first one only. Returns the macros that could not be registered and why.
    /// </summary>
    public IReadOnlyList<HotkeyFailure> Update(IEnumerable<Macro> macros)
    {
        UnregisterAll();
        var failures = new List<HotkeyFailure>();
        var owners = new Dictionary<MacroTrigger, Macro>();
        int id = FirstId;
        foreach (var macro in macros)
        {
            if (macro is null || !macro.Enabled) continue;
            var trigger = macro.Trigger;
            if (trigger is null || !Enum.IsDefined(trigger.Key))
            {
                failures.Add(new(macro, "No trigger key set."));
                continue;
            }
            if (!trigger.IsValid)
            {
                failures.Add(new(macro, $"{trigger}: {trigger.Problem}"));
                continue;
            }
            if (owners.TryGetValue(trigger, out var owner))
            {
                failures.Add(new(macro, $"{trigger} is already used by macro \"{owner.Name}\"."));
                continue;
            }
            if (_api.Register(Handle, id, HotkeyModifiers(trigger), (uint)trigger.Key, out int error))
            {
                _registered[id++] = macro;
                owners[trigger] = macro;
            }
            else
            {
                failures.Add(new(macro, error == ErrorHotkeyAlreadyRegistered
                    ? $"{trigger} is already taken by another program."
                    : $"{trigger} could not be registered (error {error})."));
                Log.Write($"Macro hotkey {trigger} for \"{macro.Name}\" failed: error {error}");
            }
        }
        _failures = failures;
        return failures;
    }

    /// <summary>RegisterHotKey fsModifiers for a trigger (always with MOD_NOREPEAT).</summary>
    public static uint HotkeyModifiers(MacroTrigger trigger)
        => ModNoRepeat | (trigger.Ctrl ? ModControl : 0) | (trigger.Shift ? ModShift : 0) | (trigger.Alt ? ModAlt : 0)
           | (trigger.Win ? ModWin : 0);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && _registered.TryGetValue((int)m.WParam, out var macro))
        {
            try { Triggered?.Invoke(macro); }
            catch (Exception e) { Log.Write($"Macro trigger handler failed: {e}"); }
        }
        base.WndProc(ref m);
    }

    void UnregisterAll()
    {
        foreach (var id in _registered.Keys) _api.Unregister(Handle, id);
        _registered.Clear();
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        UnregisterAll();
        DestroyHandle();
    }
}
