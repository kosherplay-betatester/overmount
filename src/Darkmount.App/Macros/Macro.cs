using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization;

namespace Darkmount.App.Macros;

/// <summary>
/// How a macro plays when its trigger fires. RegisterHotKey only reports presses (never releases),
/// so "repeat while held" is not possible.
/// </summary>
public enum PlaybackMode
{
    /// <summary>Play the steps once.</summary>
    Once,

    /// <summary>Play the steps <see cref="Macro.RepeatCount"/> times.</summary>
    RepeatCount,

    /// <summary>The first press starts looping the steps; the next press stops.</summary>
    Toggle,
}

/// <summary>
/// Keys a macro can be triggered by. Values are the Windows virtual-key codes. F13–F24 (which only keyboards like the
/// Dark Mount send) work alone; other keys need modifiers that keep normal typing free (see <see cref="MacroTrigger.Problem"/>).
/// Saved by name, so the names must never change: F13–F24 are what older versions wrote.
/// </summary>
public enum TriggerKey
{
    Pause = 0x13, PageUp = 0x21, PageDown = 0x22, End = 0x23, Home = 0x24, PrintScreen = 0x2C, Insert = 0x2D,
    D0 = 0x30, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    A = 0x41, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    NumPad0 = 0x60, NumPad1, NumPad2, NumPad3, NumPad4, NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,
    F1 = 0x70, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    F13 = 0x7C, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,
    ScrollLock = 0x91,
}

[Flags]
public enum MacroModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    Win = 8,
}

public enum MouseButton { Left, Right, Middle, X1, X2 }

public enum MediaKey { PlayPause, Next, Previous, Stop, VolumeUp, VolumeDown, Mute }

/// <summary>
/// The key combination that runs a macro, e.g. "F13", "Ctrl+F13" (sent by a bound Dark Mount key) or "Ctrl+Shift+K"
/// (pressed on any keyboard).
/// </summary>
public sealed record MacroTrigger(TriggerKey Key = TriggerKey.F13, bool Ctrl = false, bool Shift = false, bool Alt = false, bool Win = false)
{
    /// <summary>The keys in picker order: F13–F24 first, then letters, digits, F1–F12, the number pad and the rest.</summary>
    public static IReadOnlyList<TriggerKey> AllKeys { get; } =
    [
        .. Enum.GetValues<TriggerKey>().Where(IsF13ToF24),
        .. Enum.GetValues<TriggerKey>().Where(k => k is >= TriggerKey.A and <= TriggerKey.Z),
        .. Enum.GetValues<TriggerKey>().Where(k => k is >= TriggerKey.D0 and <= TriggerKey.D9),
        .. Enum.GetValues<TriggerKey>().Where(k => k is >= TriggerKey.F1 and <= TriggerKey.F12),
        .. Enum.GetValues<TriggerKey>().Where(k => k is >= TriggerKey.NumPad0 and <= TriggerKey.NumPad9),
        TriggerKey.Insert, TriggerKey.Home, TriggerKey.End, TriggerKey.PageUp, TriggerKey.PageDown,
        TriggerKey.PrintScreen, TriggerKey.ScrollLock, TriggerKey.Pause,
    ];

    /// <summary>F13–F24: the only keys allowed without a modifier, and the only ones a Dark Mount key can be bound to send.</summary>
    public static bool IsF13ToF24(TriggerKey key) => key is >= TriggerKey.F13 and <= TriggerKey.F24;

    /// <summary>Display name of a key: "K", "5", "F5", "Num 1", "Page Up", …</summary>
    public static string KeyName(TriggerKey key) => VirtualKeys.Name((int)key);

    /// <inheritdoc cref="IsF13ToF24(TriggerKey)"/>
    [JsonIgnore]
    public bool UsesF13ToF24 => IsF13ToF24(Key);

    [JsonIgnore]
    public bool HasModifier => Ctrl || Shift || Alt || Win;

    /// <summary>
    /// Why this trigger can't be used, or null when it can. A registered trigger swallows its keys in every app, so:
    /// F13–F24 work alone; letters, digits and the number pad need Ctrl or Win (Shift+letter is a capital, Alt+letter
    /// opens menus) but not exactly Ctrl+Alt, which is AltGr on many layouts (@, €…); Insert/Home/End/Page Up/Page Down
    /// need Ctrl, Alt or Win (Shift selects text, Shift+Insert pastes); F1–F12, Print Screen, Scroll Lock and Pause need
    /// any modifier.
    /// </summary>
    [JsonIgnore]
    public string? Problem
    {
        get
        {
            if (!Enum.IsDefined(Key)) return "Unknown key.";
            if (UsesF13ToF24) return null;
            bool typing = Key is >= TriggerKey.A and <= TriggerKey.Z or >= TriggerKey.D0 and <= TriggerKey.D9
                or >= TriggerKey.NumPad0 and <= TriggerKey.NumPad9;
            if (typing)
            {
                if (!Ctrl && !Win) return "Letters and digits need Ctrl or Win (with only Shift or Alt they'd take over typing and menus).";
                if (Ctrl && Alt && !Shift && !Win) return "Ctrl+Alt is AltGr on many keyboards (it types @, €…). Add Shift or Win.";
                return null;
            }
            if (Key is TriggerKey.Insert or TriggerKey.Home or TriggerKey.End or TriggerKey.PageUp or TriggerKey.PageDown)
                return Ctrl || Alt || Win ? null : "This key needs Ctrl, Alt or Win (with only Shift it selects or pastes text).";
            return HasModifier ? null : "Keys other than F13–F24 need Ctrl, Alt, Shift or Win.";
        }
    }

    /// <summary>True when the trigger can be used (<see cref="Problem"/> is null).</summary>
    [JsonIgnore]
    public bool IsValid => Problem is null;

    /// <summary>"Win+Ctrl+Alt+Shift+Key", e.g. "Ctrl+F13", "Ctrl+Shift+K", "Ctrl+Num 1".</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Win) sb.Append("Win+");
        if (Ctrl) sb.Append("Ctrl+");
        if (Alt) sb.Append("Alt+");
        if (Shift) sb.Append("Shift+");
        return sb.Append(KeyName(Key)).ToString();
    }

    /// <summary>
    /// Parses "F13", "Ctrl+Shift+F20", "Ctrl+Shift+K", "Win+Shift+F5", "Ctrl+Num 1", … (case, spacing and order
    /// insensitive). Only succeeds for <see cref="IsValid"/> triggers, so "K" alone is rejected.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out MacroTrigger? trigger)
    {
        trigger = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool ctrl = false, shift = false, alt = false, win = false;
        TriggerKey? key = null;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    if (ctrl) return false;
                    ctrl = true;
                    break;
                case "shift":
                    if (shift) return false;
                    shift = true;
                    break;
                case "alt":
                    if (alt) return false;
                    alt = true;
                    break;
                case "win" or "windows":
                    if (win) return false;
                    win = true;
                    break;
                default:
                    if (key is not null || !KeysByName.TryGetValue(part.Replace(" ", ""), out var k)) return false;
                    key = k;
                    break;
            }
        }
        if (key is null) return false;
        var parsed = new MacroTrigger(key.Value, ctrl, shift, alt, win);
        if (!parsed.IsValid) return false;
        trigger = parsed;
        return true;
    }

    /// <summary>Display names and enum names without spaces ("PageUp", "Num1", "NumPad1", "5", "D5"); never plain numbers like "124".</summary>
    static readonly Dictionary<string, TriggerKey> KeysByName = BuildKeysByName();

    static Dictionary<string, TriggerKey> BuildKeysByName()
    {
        var map = new Dictionary<string, TriggerKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in Enum.GetValues<TriggerKey>())
        {
            map[KeyName(k).Replace(" ", "")] = k;
            map[k.ToString()] = k;
        }
        return map;
    }
}

/// <summary>A host-side macro: a sequence of steps played when the keyboard sends <see cref="Trigger"/>.</summary>
public sealed class Macro
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public MacroTrigger Trigger { get; set; } = new();
    public List<MacroStep> Steps { get; set; } = [];
    public PlaybackMode Mode { get; set; } = PlaybackMode.Once;

    /// <summary>Number of plays in <see cref="PlaybackMode.RepeatCount"/> mode (clamped to 1–<see cref="MacroPlayer.MaxRepeatCount"/>).</summary>
    public int RepeatCount { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    /// <summary>Copy with its own step list (steps themselves are immutable records). Keeps the same <see cref="Id"/>.</summary>
    public Macro Clone() => new()
    {
        Id = Id,
        Name = Name,
        Trigger = Trigger,
        Steps = [.. Steps],
        Mode = Mode,
        RepeatCount = RepeatCount,
        Enabled = Enabled,
    };
}
