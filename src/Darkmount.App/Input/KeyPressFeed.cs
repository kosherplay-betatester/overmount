using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Darkmount.Keyboard;

namespace Darkmount.App.Input;

/// <summary>
/// Tells lighting effects which keys are pressed (Reactive, Ripple, typing heatmap). A low-level keyboard hook
/// records only the Dark Mount key id and the time of each key-down — no text is stored or sent anywhere.
/// Must be created on a thread with a message loop (the UI thread). Hook callbacks must return fast, so they only
/// update two dictionaries.
/// </summary>
public sealed class KeyPressFeed : IDisposable
{
    const int WhKeyboardLl = 13, WmKeyDown = 0x0100, WmSysKeyDown = 0x0104;
    const double HeatPerPress = 0.08, HeatHalfLifeSeconds = 20;

    readonly Stopwatch _clock;
    readonly LowLevelKeyboardProc _proc;
    readonly ConcurrentDictionary<int, double> _pressed = new();
    readonly ConcurrentDictionary<int, (double Heat, double At)> _heat = new();
    readonly Dictionary<byte, int> _usageToKey;
    readonly Darkmount.Keyboard.Lamps.KeyPress[] _log = new Darkmount.Keyboard.Lamps.KeyPress[48]; // the last presses, in order
    readonly Lock _logGate = new();
    int _logNext, _logCount;
    IntPtr _hook;

    /// <param name="clock">The animation clock the lighting engine also uses.</param>
    public KeyPressFeed(Stopwatch clock)
    {
        _clock = clock;
        _usageToKey = KeyIds.All.Where(k => k.HidUsage is not null).GroupBy(k => k.HidUsage!.Value).ToDictionary(g => g.Key, g => (int)g.First().Id);
        _proc = Callback;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write($"Key-press effects unavailable: keyboard hook failed ({Marshal.GetLastWin32Error()})");
    }

    /// <summary>Dark Mount key id → animation-clock seconds of its last press.</summary>
    public IReadOnlyDictionary<int, double> PressTimes => _pressed;

    /// <summary>Dark Mount key id → typing heat 0..1 (decays with a 20 s half-life).</summary>
    public IReadOnlyDictionary<int, double> Heat()
    {
        double now = _clock.Elapsed.TotalSeconds;
        return _heat.ToDictionary(kv => kv.Key, kv => Decay(kv.Value.Heat, now - kv.Value.At));
    }

    static double Decay(double heat, double seconds) => heat * Math.Pow(0.5, seconds / HeatHalfLifeSeconds);

    /// <summary>The presses of the last <paramref name="seconds"/>, oldest first (key ids and times only).</summary>
    public IReadOnlyList<Darkmount.Keyboard.Lamps.KeyPress> RecentPresses(double seconds)
    {
        double since = _clock.Elapsed.TotalSeconds - seconds;
        var list = new List<Darkmount.Keyboard.Lamps.KeyPress>(_logCount);
        lock (_logGate)
        {
            for (int i = 0; i < _logCount; i++)
            {
                var press = _log[(_logNext - _logCount + i + _log.Length) % _log.Length];
                if (press.At >= since) list.Add(press);
            }
        }
        return list;
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown))
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if (ScanCodes.ToHidUsage(info.ScanCode, (info.Flags & 0x01) != 0) is { } usage && _usageToKey.TryGetValue(usage, out int key))
            {
                double now = _clock.Elapsed.TotalSeconds;
                _pressed[key] = now;
                lock (_logGate)
                {
                    _log[_logNext] = new(key, now);
                    _logNext = (_logNext + 1) % _log.Length;
                    _logCount = Math.Min(_logCount + 1, _log.Length);
                }
                _heat.AddOrUpdate(key, (HeatPerPress, now), (_, old) => (Math.Min(1, Decay(old.Heat, now - old.At) + HeatPerPress), now));
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KbdLlHookStruct
    {
        public uint VkCode, ScanCode, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);
}

/// <summary>PC keyboard scan codes (set 1, as Windows reports them) → HID keyboard usages. Layout independent.</summary>
public static class ScanCodes
{
    static readonly Dictionary<uint, byte> Normal = new()
    {
        [0x01] = 0x29, [0x02] = 0x1E, [0x03] = 0x1F, [0x04] = 0x20, [0x05] = 0x21, [0x06] = 0x22, [0x07] = 0x23, [0x08] = 0x24,
        [0x09] = 0x25, [0x0A] = 0x26, [0x0B] = 0x27, [0x0C] = 0x2D, [0x0D] = 0x2E, [0x0E] = 0x2A, [0x0F] = 0x2B,
        [0x10] = 0x14, [0x11] = 0x1A, [0x12] = 0x08, [0x13] = 0x15, [0x14] = 0x17, [0x15] = 0x1C, [0x16] = 0x18, [0x17] = 0x0C,
        [0x18] = 0x12, [0x19] = 0x13, [0x1A] = 0x2F, [0x1B] = 0x30, [0x1C] = 0x28, [0x1D] = 0xE0,
        [0x1E] = 0x04, [0x1F] = 0x16, [0x20] = 0x07, [0x21] = 0x09, [0x22] = 0x0A, [0x23] = 0x0B, [0x24] = 0x0D, [0x25] = 0x0E,
        [0x26] = 0x0F, [0x27] = 0x33, [0x28] = 0x34, [0x29] = 0x35, [0x2A] = 0xE1, [0x2B] = 0x31,
        [0x2C] = 0x1D, [0x2D] = 0x1B, [0x2E] = 0x06, [0x2F] = 0x19, [0x30] = 0x05, [0x31] = 0x11, [0x32] = 0x10, [0x33] = 0x36,
        [0x34] = 0x37, [0x35] = 0x38, [0x36] = 0xE5, [0x37] = 0x55, [0x38] = 0xE2, [0x39] = 0x2C, [0x3A] = 0x39,
        [0x3B] = 0x3A, [0x3C] = 0x3B, [0x3D] = 0x3C, [0x3E] = 0x3D, [0x3F] = 0x3E, [0x40] = 0x3F, [0x41] = 0x40, [0x42] = 0x41,
        [0x43] = 0x42, [0x44] = 0x43, [0x45] = 0x53, [0x46] = 0x47, [0x47] = 0x5F, [0x48] = 0x60, [0x49] = 0x61, [0x4A] = 0x56,
        [0x4B] = 0x5C, [0x4C] = 0x5D, [0x4D] = 0x5E, [0x4E] = 0x57, [0x4F] = 0x59, [0x50] = 0x5A, [0x51] = 0x5B, [0x52] = 0x62,
        [0x53] = 0x63, [0x56] = 0x64, [0x57] = 0x44, [0x58] = 0x45,
    };

    static readonly Dictionary<uint, byte> Extended = new()
    {
        [0x1C] = 0x58, [0x1D] = 0xE4, [0x35] = 0x54, [0x37] = 0x46, [0x38] = 0xE6, [0x45] = 0x53, [0x46] = 0x48,
        [0x47] = 0x4A, [0x48] = 0x52, [0x49] = 0x4B, [0x4B] = 0x50, [0x4D] = 0x4F, [0x4F] = 0x4D, [0x50] = 0x51, [0x51] = 0x4E,
        [0x52] = 0x49, [0x53] = 0x4C, [0x5B] = 0xE3, [0x5C] = 0xE7, [0x5D] = 0x65,
    };

    public static byte? ToHidUsage(uint scanCode, bool extended) =>
        (extended ? Extended : Normal).TryGetValue(scanCode, out var usage) ? usage : null;
}
