using System.Diagnostics;
using System.Runtime.InteropServices;
using Darkmount.App;
using Darkmount.App.Macros;
using static Darkmount.App.Macros.VirtualKeys;

namespace Darkmount.Tests;

// ======================================================================== fakes

/// <summary>One ordered log shared by every fake, so tests can check the exact order of OS calls.</summary>
sealed class MacroCallLog
{
    readonly List<string> _entries = [];

    public void Add(string entry)
    {
        lock (_entries) _entries.Add(entry);
    }

    public List<string> All
    {
        get { lock (_entries) return [.. _entries]; }
    }

    /// <summary>Everything except timer waits.</summary>
    public List<string> Input => All.Where(e => !e.StartsWith("wait ")).ToList();

    public int Count(string entry) => All.Count(e => e == entry);

    public static string Down(int vk, bool extended = false) => $"down {vk:X2}{(extended ? " ext" : "")}";
    public static string Up(int vk, bool extended = false) => $"up {vk:X2}{(extended ? " ext" : "")}";
}

sealed class MacroFakeInput(MacroCallLog log) : IInputSink
{
    public void SendKeyDown(int vk, bool extended) => log.Add(MacroCallLog.Down(vk, extended));
    public void SendKeyUp(int vk, bool extended) => log.Add(MacroCallLog.Up(vk, extended));
    public void SendUnicodeChar(char c) => log.Add($"char {c}");
    public void SendMouseMove(int x, int y, bool relative) => log.Add($"move {x},{y}{(relative ? " rel" : "")}");
    public void SendMouseButton(MouseButton button, bool down) => log.Add($"{(down ? "mdown" : "mup")} {button}");
    public void SendMouseWheel(int delta) => log.Add($"wheel {delta}");
}

sealed class MacroFakeShell(MacroCallLog log) : IShell
{
    public Exception? LaunchThrows;

    public void Launch(string path, string? arguments, string? workingDirectory)
    {
        log.Add($"launch {path}|{arguments}|{workingDirectory}");
        if (LaunchThrows is not null) throw LaunchThrows;
    }

    public void OpenUrl(string url) => log.Add($"url {url}");
    public void OpenFolder(string path) => log.Add($"folder {path}");
}

/// <summary>Reports Ctrl as held for the first <see cref="CtrlHeldForChecks"/> checks.</summary>
sealed class MacroFakeKeyState : IKeyState
{
    int _ctrlChecks;
    public int CtrlHeldForChecks;

    /// <summary>The key reported as held (Ctrl unless a test says otherwise).</summary>
    public int HeldVk = VirtualKeys.Control;

    public bool IsDown(int vk) => vk == HeldVk && Interlocked.Increment(ref _ctrlChecks) <= CtrlHeldForChecks;
}

/// <summary>Logs each wait; only really sleeps (up to <see cref="RealWaitCapMs"/>) when a test needs concurrency.</summary>
sealed class MacroFakeTimer(MacroCallLog log) : IMacroTimer
{
    public int RealWaitCapMs;

    public bool Wait(int milliseconds, CancellationToken token)
    {
        log.Add($"wait {milliseconds}");
        if (RealWaitCapMs > 0) token.WaitHandle.WaitOne(Math.Min(milliseconds, RealWaitCapMs));
        return !token.IsCancellationRequested;
    }
}

sealed class MacroFakeHotkeyApi : IHotkeyApi
{
    public readonly Dictionary<int, (uint Modifiers, uint Vk)> Registered = [];
    public readonly HashSet<uint> TakenByOtherPrograms = [];

    public bool Register(IntPtr hwnd, int id, uint modifiers, uint vk, out int error)
    {
        if (TakenByOtherPrograms.Contains(vk))
        {
            error = 1409; // ERROR_HOTKEY_ALREADY_REGISTERED
            return false;
        }
        Registered.Add(id, (modifiers, vk));
        error = 0;
        return true;
    }

    public void Unregister(IntPtr hwnd, int id) => Registered.Remove(id);
}

sealed class MacroPlayerRig : IDisposable
{
    public readonly MacroCallLog Log = new();
    public readonly MacroFakeShell Shell;
    public readonly MacroFakeKeyState Keys = new();
    public readonly MacroFakeTimer Timer;
    public readonly MacroPlayer Player;
    public readonly List<(Macro Macro, MacroRunResult Result)> Finished = [];

    public MacroPlayerRig(int realWaitCapMs = 0)
    {
        Darkmount.App.Log.Enabled = false;
        Shell = new MacroFakeShell(Log);
        Timer = new MacroFakeTimer(Log) { RealWaitCapMs = realWaitCapMs };
        Player = new MacroPlayer(new MacroFakeInput(Log), Shell, Keys, Timer);
        Player.Finished += (m, r) => { lock (Finished) Finished.Add((m, r)); };
    }

    public void PlayToEnd(Macro macro)
    {
        Assert.Equal(TriggerResult.Started, Player.Trigger(macro));
        Assert.True(Player.WaitForIdle(TimeSpan.FromSeconds(5)), "macro did not finish");
    }

    public static void WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "condition not reached in time");
            Thread.Sleep(2);
        }
    }

    public void Dispose() => Player.Dispose();
}

static class TestMacro
{
    public static Macro Of(params MacroStep[] steps) => new() { Name = "test", Steps = [.. steps] };
}

// ======================================================================== trigger

public class MacroTriggerTests
{
    [Theory]
    [InlineData(TriggerKey.F13, false, false, false, "F13")]
    [InlineData(TriggerKey.F13, true, false, false, "Ctrl+F13")]
    [InlineData(TriggerKey.F18, false, true, false, "Shift+F18")]
    [InlineData(TriggerKey.F24, true, true, true, "Ctrl+Alt+Shift+F24")]
    [InlineData(TriggerKey.K, true, false, true, "Ctrl+Alt+K")]
    [InlineData(TriggerKey.NumPad1, true, false, false, "Ctrl+Num 1")]
    [InlineData(TriggerKey.D7, false, true, false, "Shift+7")]
    [InlineData(TriggerKey.PageUp, false, false, true, "Alt+Page Up")]
    public void ToString_formats_modifiers_then_key(TriggerKey key, bool ctrl, bool shift, bool alt, string expected)
        => Assert.Equal(expected, new MacroTrigger(key, ctrl, shift, alt).ToString());

    [Fact]
    public void ToString_puts_Win_first()
    {
        Assert.Equal("Win+Shift+F5", new MacroTrigger(TriggerKey.F5, Shift: true, Win: true).ToString());
        Assert.Equal("Win+Ctrl+Alt+Shift+F13", new MacroTrigger(TriggerKey.F13, true, true, true, true).ToString());
    }

    [Theory]
    [InlineData("F13", TriggerKey.F13, false, false, false)]
    [InlineData("Ctrl+F13", TriggerKey.F13, true, false, false)]
    [InlineData(" shift + alt + f20 ", TriggerKey.F20, false, true, true)]
    [InlineData("Control+F24", TriggerKey.F24, true, false, false)]
    [InlineData("Alt+Shift+Ctrl+F16", TriggerKey.F16, true, true, true)]
    [InlineData("Ctrl+Alt+Shift+K", TriggerKey.K, true, true, true)]
    [InlineData("ctrl+a", TriggerKey.A, true, false, false)]
    [InlineData("Ctrl+Num 1", TriggerKey.NumPad1, true, false, false)]
    [InlineData("Ctrl+num1", TriggerKey.NumPad1, true, false, false)]
    [InlineData("Ctrl+NumPad1", TriggerKey.NumPad1, true, false, false)]
    [InlineData("Ctrl+5", TriggerKey.D5, true, false, false)]
    [InlineData("Shift+F1", TriggerKey.F1, false, true, false)]
    [InlineData("Ctrl+Shift+F12", TriggerKey.F12, true, true, false)]
    [InlineData("Ctrl+Page Down", TriggerKey.PageDown, true, false, false)]
    [InlineData("Ctrl+pageup", TriggerKey.PageUp, true, false, false)]
    [InlineData("Alt+Print Screen", TriggerKey.PrintScreen, false, false, true)]
    [InlineData("Ctrl+Scroll Lock", TriggerKey.ScrollLock, true, false, false)]
    [InlineData("Ctrl+Pause", TriggerKey.Pause, true, false, false)]
    [InlineData("Alt+Insert", TriggerKey.Insert, false, false, true)]
    public void TryParse_accepts_trigger_keys_with_modifiers(string text, TriggerKey key, bool ctrl, bool shift, bool alt)
    {
        Assert.True(MacroTrigger.TryParse(text, out var trigger));
        Assert.Equal(new MacroTrigger(key, ctrl, shift, alt), trigger);
    }

    [Theory]
    [InlineData("Win+Shift+F5", TriggerKey.F5, false, true)]
    [InlineData("win+f13", TriggerKey.F13, false, false)]
    [InlineData("Windows+Ctrl+K", TriggerKey.K, true, false)]
    public void TryParse_accepts_Win(string text, TriggerKey key, bool ctrl, bool shift)
    {
        Assert.True(MacroTrigger.TryParse(text, out var trigger));
        Assert.Equal(new MacroTrigger(key, ctrl, shift, Win: true), trigger);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("F12")]
    [InlineData("F25")]
    [InlineData("A")]
    [InlineData("k")]
    [InlineData("5")]
    [InlineData("Num 1")]
    [InlineData("Page Up")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Delete")]
    [InlineData("Ctrl+Space")]
    [InlineData("Ctrl+12")]
    [InlineData("F13+F14")]
    [InlineData("Ctrl+K+L")]
    [InlineData("Ctrl++F13")]
    [InlineData("Ctrl+Ctrl+F13")]
    [InlineData("Win+Win+K")]
    [InlineData("124")]
    [InlineData("Ctrl+124")]
    public void TryParse_rejects_anything_else(string? text)
    {
        Assert.False(MacroTrigger.TryParse(text, out var trigger));
        Assert.Null(trigger);
    }

    [Fact]
    public void Every_valid_trigger_round_trips_through_text_and_invalid_ones_do_not_parse()
    {
        int valid = 0;
        foreach (var key in Enum.GetValues<TriggerKey>())
        for (int mods = 0; mods < 16; mods++)
        {
            var t = new MacroTrigger(key, (mods & 1) != 0, (mods & 2) != 0, (mods & 4) != 0, (mods & 8) != 0);
            if (t.IsValid)
            {
                Assert.True(MacroTrigger.TryParse(t.ToString(), out var back), t.ToString());
                Assert.Equal(t, back);
                valid++;
            }
            else Assert.False(MacroTrigger.TryParse(t.ToString(), out _), t.ToString());
        }
        // F13–F24: any of the 16 modifier sets; letters/digits/numpad (46): Ctrl or Win, not exactly Ctrl+Alt (11);
        // Insert/Home/End/Page Up/Page Down (5): Ctrl, Alt or Win (14); F1–F12, Print Screen, Scroll Lock, Pause (15): any (15).
        Assert.Equal(12 * 16 + 46 * 11 + 5 * 14 + 15 * 15, valid);
    }

    [Fact]
    public void Trigger_keys_are_the_windows_virtual_key_codes()
    {
        Assert.Equal(0x7C, (int)TriggerKey.F13);
        Assert.Equal(0x87, (int)TriggerKey.F24);
        Assert.Equal(0x41, (int)TriggerKey.A);
        Assert.Equal(0x5A, (int)TriggerKey.Z);
        Assert.Equal(0x30, (int)TriggerKey.D0);
        Assert.Equal(0x39, (int)TriggerKey.D9);
        Assert.Equal(0x70, (int)TriggerKey.F1);
        Assert.Equal(0x7B, (int)TriggerKey.F12);
        Assert.Equal(0x60, (int)TriggerKey.NumPad0);
        Assert.Equal(0x69, (int)TriggerKey.NumPad9);
        Assert.Equal(VirtualKeys.Insert, (int)TriggerKey.Insert);
        Assert.Equal(VirtualKeys.Home, (int)TriggerKey.Home);
        Assert.Equal(VirtualKeys.End, (int)TriggerKey.End);
        Assert.Equal(VirtualKeys.PageUp, (int)TriggerKey.PageUp);
        Assert.Equal(VirtualKeys.PageDown, (int)TriggerKey.PageDown);
        Assert.Equal(VirtualKeys.Pause, (int)TriggerKey.Pause);
        Assert.Equal(VirtualKeys.PrintScreen, (int)TriggerKey.PrintScreen);
        Assert.Equal(0x91, (int)TriggerKey.ScrollLock);
        Assert.Equal(12, Enum.GetValues<TriggerKey>().Count(MacroTrigger.IsF13ToF24));
    }

    [Fact]
    public void Picker_lists_every_key_once_with_F13_to_F24_first()
    {
        Assert.Equal(Enum.GetValues<TriggerKey>().Order(), MacroTrigger.AllKeys.Order());
        Assert.Equal(Enum.GetValues<TriggerKey>().Length, MacroTrigger.AllKeys.Distinct().Count());
        Assert.All(MacroTrigger.AllKeys.Take(12), k => Assert.True(MacroTrigger.IsF13ToF24(k)));
    }

    [Theory]
    [InlineData(TriggerKey.A, false, false, false, false, false)]   // plain letter: would hijack typing
    [InlineData(TriggerKey.D1, false, false, false, false, false)]
    [InlineData(TriggerKey.F5, false, false, false, false, false)]
    [InlineData(TriggerKey.NumPad3, false, false, false, false, false)]
    [InlineData(TriggerKey.Home, false, false, false, false, false)]
    [InlineData(TriggerKey.A, true, false, false, false, true)]
    [InlineData(TriggerKey.K, false, true, false, false, false)]    // Shift+K is a capital K
    [InlineData(TriggerKey.D7, false, true, false, false, false)]   // Shift+7 is "&"
    [InlineData(TriggerKey.K, false, false, true, false, false)]    // Alt+K opens app menus
    [InlineData(TriggerKey.K, true, false, true, false, false)]     // Ctrl+Alt = AltGr on many layouts
    [InlineData(TriggerKey.K, true, true, true, false, true)]
    [InlineData(TriggerKey.K, false, false, false, true, true)]     // Win+K
    [InlineData(TriggerKey.Home, false, true, false, false, false)] // Shift+Home selects text
    [InlineData(TriggerKey.Insert, false, true, false, false, false)] // Shift+Insert pastes
    [InlineData(TriggerKey.Home, false, false, true, false, true)]
    [InlineData(TriggerKey.F5, false, true, false, false, true)]
    [InlineData(TriggerKey.F5, false, false, true, false, true)]
    [InlineData(TriggerKey.NumPad3, false, false, false, true, true)] // Win counts as a modifier
    [InlineData(TriggerKey.F13, false, false, false, false, true)]   // F13–F24 work alone
    [InlineData(TriggerKey.F24, false, false, false, false, true)]
    [InlineData(TriggerKey.F13, false, false, false, true, true)]
    public void IsValid_needs_a_modifier_except_for_F13_to_F24(TriggerKey key, bool ctrl, bool shift, bool alt, bool win, bool expected)
        => Assert.Equal(expected, new MacroTrigger(key, ctrl, shift, alt, win).IsValid);

    [Fact]
    public void Unknown_key_codes_are_invalid_even_with_modifiers()
    {
        Assert.False(new MacroTrigger((TriggerKey)VirtualKeys.Delete, Ctrl: true).IsValid);
        Assert.False(new MacroTrigger((TriggerKey)0x88, Ctrl: true).IsValid);
    }

    [Fact]
    public void Free_trigger_only_picks_F13_to_F24()
    {
        var macros = new List<Macro>
        {
            new() { Trigger = new(TriggerKey.F13) },
            new() { Trigger = new(TriggerKey.A, Ctrl: true) },
            new() { Trigger = new(TriggerKey.F14, Win: true) },
        };

        Assert.Equal(new MacroTrigger(TriggerKey.F14), MacroManager.FreeTrigger(macros));

        var all = MacroTrigger.AllKeys.Where(MacroTrigger.IsF13ToF24).Select(k => new Macro { Trigger = new(k) }).ToList();
        Assert.Equal(new MacroTrigger(TriggerKey.F24, Ctrl: true), MacroManager.FreeTrigger(all));
    }
}

// ======================================================================== JSON + store

public sealed class MacroStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "darkmount-macro-tests-" + Guid.NewGuid().ToString("N"));

    public MacroStoreTests()
    {
        Log.Enabled = false;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    public static TheoryData<MacroStep> AllSteps => new()
    {
        new KeyTapStep(Escape, MacroModifiers.Ctrl | MacroModifiers.Shift),
        new KeyTapStep(A, MacroModifiers.None, HoldMs: 80),
        new KeyDownStep(LShift),
        new KeyUpStep(LShift),
        new TypeTextStep("Hello \"world\"\r\nZeile 2 – ünïcödé 😀", PerCharDelayMs: 15),
        new DelayStep(250),
        new MouseClickStep(MouseButton.Right),
        new MouseClickStep(MouseButton.X2, 1920, -40),
        new MouseMoveStep(100, 200),
        new MouseMoveStep(-5, 7, Relative: true),
        new ScrollStep(-3),
        new LaunchProgramStep(@"C:\Program Files\App\app.exe", "--flag \"x y\"", @"C:\Temp"),
        new LaunchProgramStep("notepad.exe"),
        new OpenUrlStep("https://example.com/?q=1&r=2"),
        new OpenFolderStep(@"C:\Users\Public"),
        new MediaKeyStep(MediaKey.VolumeUp),
    };

    [Theory]
    [MemberData(nameof(AllSteps))]
    public void Every_step_type_round_trips_through_json(MacroStep step)
    {
        var json = MacroStore.Serialize([new Macro { Name = "x", Steps = [step] }]);

        var back = Assert.Single(MacroStore.Deserialize(json));
        Assert.Equal(step, Assert.Single(back.Steps));
        Assert.Contains("\"type\":", json);
    }

    [Fact]
    public void Round_trip_data_covers_every_step_type()
    {
        var covered = ((IEnumerable<object[]>)AllSteps).Select(row => row[0].GetType()).ToHashSet();
        var all = typeof(MacroStep).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(MacroStep)) && !t.IsAbstract);
        Assert.All(all, t => Assert.Contains(t, covered));
    }

    [Fact]
    public void Macro_properties_round_trip()
    {
        var m = new Macro
        {
            Name = "Spam jump",
            Trigger = new MacroTrigger(TriggerKey.F21, Ctrl: true, Alt: true),
            Mode = PlaybackMode.Toggle,
            RepeatCount = 7,
            Enabled = false,
            Steps = [new KeyTapStep(Space), new DelayStep(100)],
        };

        var back = Assert.Single(MacroStore.Deserialize(MacroStore.Serialize([m])));

        Assert.Equal(m.Id, back.Id);
        Assert.Equal(m.Name, back.Name);
        Assert.Equal(m.Trigger, back.Trigger);
        Assert.Equal(m.Mode, back.Mode);
        Assert.Equal(m.RepeatCount, back.RepeatCount);
        Assert.Equal(m.Enabled, back.Enabled);
        Assert.Equal(m.Steps, back.Steps);
    }

    [Fact]
    public void Json_is_readable()
    {
        var json = MacroStore.Serialize([new Macro { Trigger = new(TriggerKey.F13, Ctrl: true), Mode = PlaybackMode.Toggle, Steps = [new KeyTapStep(A, MacroModifiers.Ctrl | MacroModifiers.Shift)] }]);

        Assert.Contains("\"type\": \"KeyTap\"", json);
        Assert.Contains("\"Mode\": \"Toggle\"", json);
        Assert.Contains("\"Key\": \"F13\"", json);
        Assert.Contains("\"Modifiers\": \"Ctrl, Shift\"", json);
        Assert.DoesNotContain("IsValid", json);
        Assert.DoesNotContain("UsesF13ToF24", json);
        Assert.DoesNotContain("HasModifier", json);
    }

    [Fact]
    public void Macro_files_from_older_versions_load_unchanged()
    {
        // As 1.5 wrote them: F13–F24 by name (or a number when hand-edited), Ctrl/Shift/Alt, no Win.
        var json = """
            [
              { "Name": "a", "Trigger": { "Key": "F13", "Ctrl": false, "Shift": false, "Alt": false }, "Steps": [], "Enabled": true },
              { "Name": "b", "Trigger": { "Key": "F21", "Ctrl": true, "Shift": false, "Alt": true }, "Steps": [], "Enabled": true },
              { "Name": "c", "Trigger": { "Key": "F24", "Ctrl": true, "Shift": true, "Alt": true }, "Steps": [], "Enabled": false },
              { "Name": "d", "Trigger": { "Key": 125, "Shift": true }, "Steps": [], "Enabled": true }
            ]
            """;

        var list = MacroStore.Deserialize(json);

        Assert.Equal(
            [new MacroTrigger(TriggerKey.F13), new MacroTrigger(TriggerKey.F21, Ctrl: true, Alt: true),
             new MacroTrigger(TriggerKey.F24, true, true, true), new MacroTrigger(TriggerKey.F14, Shift: true)],
            list.Select(m => m.Trigger));
        Assert.Equal([true, true, false, true], list.Select(m => m.Enabled));
        Assert.All(list, m => Assert.False(m.Trigger.Win));
    }

    [Fact]
    public void Any_keyboard_triggers_round_trip_through_json()
    {
        var m = new Macro { Trigger = new MacroTrigger(TriggerKey.K, Ctrl: true, Alt: true, Win: true) };

        var json = MacroStore.Serialize([m]);
        var back = Assert.Single(MacroStore.Deserialize(json));

        Assert.Equal(m.Trigger, back.Trigger);
        Assert.True(back.Enabled);
        Assert.Contains("\"Key\": \"K\"", json);
        Assert.Contains("\"Win\": true", json);
    }

    [Fact]
    public void Loading_a_plain_key_trigger_disables_the_macro()
    {
        var back = Assert.Single(MacroStore.Deserialize("""[{ "Name": "x", "Trigger": { "Key": "K" }, "Enabled": true }]"""));

        Assert.False(back.Enabled);
        Assert.True(back.Trigger.IsValid);
    }

    [Fact]
    public void Hand_edited_json_may_put_the_type_anywhere()
    {
        var back = MacroStore.Deserialize("""[{ "Name": "x", "Steps": [ { "Ms": 5, "type": "Delay" } ] }]""");

        Assert.Equal(new DelayStep(5), Assert.Single(Assert.Single(back).Steps));
    }

    [Fact]
    public void Loaded_macros_are_normalised()
    {
        var id = Guid.NewGuid();
        var json = $$"""
            [
              { "Id": "{{id}}", "Steps": null, "RepeatCount": 0 },
              { "Id": "{{id}}", "Name": "dup", "Trigger": null, "Steps": [ null, { "type": "Delay", "Ms": 1 } ] },
              null,
              { "Id": "00000000-0000-0000-0000-000000000000", "Trigger": { "Key": 5 } }
            ]
            """;

        var list = MacroStore.Deserialize(json);

        Assert.Equal(3, list.Count);
        Assert.Equal("", list[0].Name);
        Assert.Empty(list[0].Steps);
        Assert.Equal(1, list[0].RepeatCount);
        Assert.NotEqual(id, list[1].Id);
        Assert.Single(list[1].Steps);
        Assert.False(list[1].Enabled);
        Assert.NotEqual(Guid.Empty, list[2].Id);
        Assert.False(list[2].Enabled);
        Assert.True(list[2].Trigger.IsValid);
        Assert.Equal(3, list.Select(m => m.Id).Distinct().Count());
    }

    [Fact]
    public void Save_then_Load_round_trips_and_leaves_no_temp_file()
    {
        var path = Path.Combine(_dir, "sub", "macros.json");
        var macros = MacroLibrary.Examples();

        Assert.True(MacroStore.Save(path, macros));
        var back = MacroStore.Load(path);

        Assert.Equal(macros.Select(m => (m.Id, m.Name)), back.Select(m => (m.Id, m.Name)));
        Assert.Equal(["macros.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(f => Path.GetFileName(f)));
    }

    [Fact]
    public void Missing_file_loads_as_empty_list() => Assert.Empty(MacroStore.Load(Path.Combine(_dir, "nope.json")));

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""[{ "Steps": [ { "type": "RunCommand", "Command": "format c:" } ] }]""")]
    [InlineData("""[{ "Steps": [ { "Ms": 5 } ] }]""")]
    [InlineData("42")]
    public void Corrupt_file_loads_as_empty_list_and_is_kept_aside(string content)
    {
        var path = Path.Combine(_dir, "macros.json");
        File.WriteAllText(path, content);

        Assert.Empty(MacroStore.Load(path));
        Assert.Equal(content, File.ReadAllText(path + ".corrupt"));
    }

    [Fact]
    public async Task Briefly_locked_file_is_retried_instead_of_loading_empty()
    {
        var path = Path.Combine(_dir, "macros.json");
        MacroStore.Save(path, [new Macro()]);
        var locker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); // e.g. antivirus or cloud sync
        var unlock = Task.Run(async () => { await Task.Delay(60); locker.Dispose(); });

        Assert.Single(MacroStore.Load(path));
        await unlock;
        Assert.False(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Persistently_locked_file_loads_as_empty_list_without_throwing()
    {
        var path = Path.Combine(_dir, "macros.json");
        MacroStore.Save(path, [new Macro()]);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(MacroStore.Load(path));
        }
        Assert.Single(MacroStore.Load(path));
    }

    [Fact]
    public void Save_never_throws()
    {
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "a file where a directory should be");

        Assert.False(MacroStore.Save(Path.Combine(blocker, "macros.json"), [new Macro()]));
    }

    [Fact]
    public void Default_path_is_in_appdata()
        => Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OverMount", "macros.json"), MacroStore.DefaultPath);
}

// ======================================================================== library + model helpers

public class MacroLibraryTests
{
    [Fact]
    public void Examples_are_three_disabled_macros_with_distinct_triggers()
    {
        var examples = MacroLibrary.Examples();

        Assert.Equal(3, examples.Count);
        Assert.All(examples, m =>
        {
            Assert.False(m.Enabled);
            Assert.NotEmpty(m.Name);
            Assert.NotEmpty(m.Steps);
            Assert.True(m.Trigger.IsValid);
        });
        Assert.Equal(3, examples.Select(m => m.Trigger).Distinct().Count());
        Assert.Equal(3, examples.Select(m => m.Id).Distinct().Count());
        Assert.Contains(examples, m => m.Steps.Contains(new KeyTapStep(Escape, MacroModifiers.Ctrl | MacroModifiers.Shift)));
        Assert.Contains(examples, m => m.Steps.Contains(new KeyTapStep(S, MacroModifiers.Win | MacroModifiers.Shift)));
        Assert.Contains(examples, m => m.Steps.OfType<TypeTextStep>().Any());
    }

    [Fact]
    public void Examples_are_fresh_copies() => Assert.NotEqual(MacroLibrary.Examples()[0].Id, MacroLibrary.Examples()[0].Id);

    [Fact]
    public void Clone_copies_the_step_list()
    {
        var m = TestMacro.Of(new DelayStep(1));
        var c = m.Clone();
        c.Steps.Add(new DelayStep(2));

        Assert.Equal(m.Id, c.Id);
        Assert.Single(m.Steps);
    }

    [Theory]
    [InlineData(Right, true)]
    [InlineData(Left, true)]
    [InlineData(Home, true)]
    [InlineData(PageDown, true)]
    [InlineData(Insert, true)]
    [InlineData(Delete, true)]
    [InlineData(RControl, true)]
    [InlineData(RMenu, true)]
    [InlineData(LWin, true)]
    [InlineData(Divide, true)]
    [InlineData(NumLock, true)]
    [InlineData(MediaPlayPause, true)]
    [InlineData(VolumeUp, true)]
    [InlineData(A, false)]
    [InlineData(Return, false)]
    [InlineData(LControl, false)]
    [InlineData(RShift, false)]
    [InlineData(LShift, false)]
    [InlineData(Escape, false)]
    public void Extended_keys_are_navigation_right_side_and_media_keys(int vk, bool extended)
        => Assert.Equal(extended, VirtualKeys.IsExtended(vk));

    [Fact]
    public void Steps_describe_themselves()
    {
        Assert.Equal("Press Ctrl+Shift+Esc", new KeyTapStep(Escape, MacroModifiers.Ctrl | MacroModifiers.Shift).Describe());
        Assert.Equal("Press Win+Shift+S", new KeyTapStep(S, MacroModifiers.Win | MacroModifiers.Shift).Describe());
        Assert.Equal("Press A (hold 80 ms)", new KeyTapStep(A, HoldMs: 80).Describe());
        Assert.Equal("Wait 250 ms", new DelayStep(250).Describe());
        Assert.Equal("Hold down Left Shift", new KeyDownStep(LShift).Describe());
        Assert.Equal("Left click at 10, 20", new MouseClickStep(MouseButton.Left, 10, 20).Describe());
        Assert.Equal("Scroll down 3", new ScrollStep(-3).Describe());
        Assert.Equal("Media: Volume up", new MediaKeyStep(MediaKey.VolumeUp).Describe());
    }
}

// ======================================================================== recorder post-processing

public class MacroRecorderTests
{
    static RecordedInput D(int vk, long t) => RecordedInput.KeyDown(vk, t);
    static RecordedInput U(int vk, long t) => RecordedInput.KeyUp(vk, t);

    [Fact]
    public void Down_up_of_the_same_key_becomes_a_tap_that_keeps_its_hold_time()
        => Assert.Equal([new KeyTapStep(A, HoldMs: 80)], MacroRecorder.Simplify([D(A, 1000), U(A, 1080)]));

    [Fact]
    public void Gaps_between_events_become_delays()
        => Assert.Equal(
            [new KeyTapStep(A, HoldMs: 80), new DelayStep(120), new KeyTapStep(C, HoldMs: 60)],
            MacroRecorder.Simplify([D(A, 0), U(A, 80), D(C, 200), U(C, 260)]));

    [Fact]
    public void A_key_with_other_events_inside_stays_down_and_up()
        => Assert.Equal(
            [new KeyDownStep(LShift), new DelayStep(50), new KeyTapStep(A, HoldMs: 50), new DelayStep(50), new KeyUpStep(LShift)],
            MacroRecorder.Simplify([D(LShift, 0), D(A, 50), U(A, 100), U(LShift, 150)]));

    [Fact]
    public void Overlapping_keys_are_not_collapsed()
        => Assert.Equal(
            [new KeyDownStep(A), new DelayStep(30), new KeyDownStep(C), new DelayStep(30), new KeyUpStep(A), new DelayStep(30), new KeyUpStep(C)],
            MacroRecorder.Simplify([D(A, 0), D(C, 30), U(A, 60), U(C, 90)]));

    [Fact]
    public void Delays_and_holds_are_capped_at_five_seconds()
        => Assert.Equal(
            [new KeyTapStep(A, HoldMs: 5000), new DelayStep(5000), new KeyTapStep(C, HoldMs: 50)],
            MacroRecorder.Simplify([D(A, 0), U(A, 9000), D(C, 29_000), U(C, 29_050)]));

    [Fact]
    public void Tiny_delays_are_merged_into_the_next_one()
        => Assert.Equal(
            [new KeyTapStep(A), new KeyTapStep(C), new DelayStep(100), new KeyTapStep(S, HoldMs: 50)],
            MacroRecorder.Simplify([D(A, 0), U(A, 3), D(C, 7), U(C, 9), D(S, 100), U(S, 150)]));

    [Fact]
    public void Auto_repeat_downs_are_dropped()
        => Assert.Equal([new KeyTapStep(A, HoldMs: 600)], MacroRecorder.Simplify([D(A, 0), D(A, 500), D(A, 530), D(A, 560), U(A, 600)]));

    [Fact]
    public void Orphan_ups_and_keys_still_held_at_stop_are_dropped()
        => Assert.Equal(
            [new KeyTapStep(A, HoldMs: 40)],
            MacroRecorder.Simplify([U(Return, 0), D(A, 20), U(A, 60), D(LControl, 900)]));

    [Fact]
    public void Mouse_clicks_are_recorded_at_the_press_position()
        => Assert.Equal(
            [new MouseClickStep(MouseButton.Left, 100, 200), new DelayStep(300), new KeyTapStep(A, HoldMs: 20)],
            MacroRecorder.Simplify([
                RecordedInput.MouseDown(MouseButton.Left, 100, 200, 0),
                RecordedInput.MouseUp(MouseButton.Left, 90),
                D(A, 300), U(A, 320),
                RecordedInput.MouseDown(MouseButton.Left, 5, 5, 400), // the click on "Stop"
            ]));

    [Fact]
    public void Nothing_recorded_gives_no_steps() => Assert.Empty(MacroRecorder.Simplify([]));

    [Fact]
    public void Stop_without_start_returns_nothing()
    {
        using var recorder = new MacroRecorder();
        Assert.False(recorder.IsRecording);
        Assert.Empty(recorder.Stop());
    }
}

// ======================================================================== player

public class MacroPlayerTests
{
    static string Down(int vk, bool ext = false) => MacroCallLog.Down(vk, ext);
    static string Up(int vk, bool ext = false) => MacroCallLog.Up(vk, ext);

    [Fact]
    public void Key_tap_presses_modifiers_first_and_releases_them_in_reverse()
    {
        using var rig = new MacroPlayerRig();
        rig.PlayToEnd(TestMacro.Of(new KeyTapStep(Escape, MacroModifiers.Ctrl | MacroModifiers.Shift)));

        Assert.Equal([Down(LControl), Down(LShift), Down(Escape), Up(Escape), Up(LShift), Up(LControl)], rig.Log.All);
    }

    [Fact]
    public void Win_modifier_uses_the_extended_left_win_key()
    {
        using var rig = new MacroPlayerRig();
        rig.PlayToEnd(TestMacro.Of(new KeyTapStep(S, MacroModifiers.Win | MacroModifiers.Shift)));

        Assert.Equal([Down(LWin, true), Down(LShift), Down(S), Up(S), Up(LShift), Up(LWin, true)], rig.Log.All);
    }

    [Theory]
    [InlineData(Right, true)]
    [InlineData(RControl, true)]
    [InlineData(Delete, true)]
    [InlineData(A, false)]
    [InlineData(RShift, false)]
    public void Keys_are_sent_with_the_right_extended_flag(int vk, bool extended)
    {
        using var rig = new MacroPlayerRig();
        rig.PlayToEnd(TestMacro.Of(new KeyDownStep(vk), new KeyUpStep(vk)));

        Assert.Equal([Down(vk, extended), Up(vk, extended)], rig.Log.All);
    }

    [Fact]
    public void Tap_hold_time_waits_between_down_and_up()
    {
        using var rig = new MacroPlayerRig();
        rig.PlayToEnd(TestMacro.Of(new KeyTapStep(A, HoldMs: 80)));

        Assert.Equal([Down(A), "wait 80", Up(A)], rig.Log.All);
    }

    [Fact]
    public void Text_is_typed_as_unicode_events_with_enter_and_tab_as_keys()
    {
        using var rig = new MacroPlayerRig();
        rig.PlayToEnd(TestMacro.Of(new TypeTextStep("Hé\r\n€\t!\nx\r")));

        Assert.Equal(
            ["char H", "char é", Down(Return), Up(Return), "char €", Down(Tab), Up(Tab), "char !", Down(Return), Up(Return), "char x", Down(Return), Up(Return)],
            rig.Log.All);
    }

    [Fact]
    public void Per_character_delay_goes_between_characters_but_not_inside_a_surrogate_pair()
    {
        using var rig = new MacroPlayerRig();
        rig.PlayToEnd(TestMacro.Of(new TypeTextStep("a\U0001F600b", PerCharDelayMs: 25)));

        Assert.Equal(["char a", "wait 25", "char \uD83D", "char \uDE00", "wait 25", "char b"], rig.Log.All);
    }

    [Fact]
    public void All_step_kinds_play_in_order()
    {
        using var rig = new MacroPlayerRig();
        rig.PlayToEnd(TestMacro.Of(
            new KeyDownStep(LShift),
            new DelayStep(40),
            new KeyUpStep(LShift),
            new MouseClickStep(MouseButton.Right, 100, 200),
            new MouseClickStep(MouseButton.Left),
            new MouseMoveStep(-5, 7, Relative: true),
            new MouseMoveStep(300, 400),
            new ScrollStep(-3),
            new MediaKeyStep(MediaKey.PlayPause),
            new LaunchProgramStep(@"C:\Tools\app.exe", "--x", @"C:\Tools"),
            new OpenUrlStep("https://example.com/"),
            new OpenFolderStep(@"C:\Users")));

        Assert.Equal(
            [
                Down(LShift), "wait 40", Up(LShift),
                "move 100,200", "mdown Right", "mup Right",
                "mdown Left", "mup Left",
                "move -5,7 rel", "move 300,400",
                "wheel -360",
                Down(MediaPlayPause, true), Up(MediaPlayPause, true),
                @"launch C:\Tools\app.exe|--x|C:\Tools",
                "url https://example.com/",
                @"folder C:\Users",
            ],
            rig.Log.All);
    }

    [Fact]
    public void RepeatCount_plays_the_steps_n_times()
    {
        using var rig = new MacroPlayerRig();
        var m = TestMacro.Of(new KeyTapStep(A), new DelayStep(15));
        m.Mode = PlaybackMode.RepeatCount;
        m.RepeatCount = 3;

        rig.PlayToEnd(m);

        Assert.Equal(3, rig.Log.Count(Down(A)));
        Assert.Equal(3, rig.Log.Count(Up(A)));
        Assert.Equal(3, rig.Log.Count("wait 15"));
        Assert.DoesNotContain($"wait {MacroPlayer.MinLoopGapMs}", rig.Log.All);
    }

    [Fact]
    public void Once_ignores_RepeatCount()
    {
        using var rig = new MacroPlayerRig();
        var m = TestMacro.Of(new KeyTapStep(A));
        m.RepeatCount = 5;

        rig.PlayToEnd(m);

        Assert.Equal(1, rig.Log.Count(Down(A)));
    }

    [Fact]
    public void Loops_that_never_wait_get_a_minimum_gap_between_iterations()
    {
        using var rig = new MacroPlayerRig();
        var m = TestMacro.Of(new KeyTapStep(A));
        m.Mode = PlaybackMode.RepeatCount;
        m.RepeatCount = 3;

        rig.PlayToEnd(m);

        Assert.Equal([Down(A), Up(A), "wait 10", Down(A), Up(A), "wait 10", Down(A), Up(A)], rig.Log.All);
    }

    [Fact]
    public void Keys_left_down_by_the_macro_are_released_when_it_ends()
    {
        using var rig = new MacroPlayerRig();
        rig.PlayToEnd(TestMacro.Of(new KeyDownStep(LShift), new KeyDownStep(A), new MouseClickStep()));

        Assert.Equal([Down(LShift), Down(A), "mdown Left", "mup Left", Up(A), Up(LShift)], rig.Log.All);
        Assert.Equal(MacroRunResult.Completed, Assert.Single(rig.Finished).Result);
    }

    [Fact]
    public void Stopping_releases_held_keys_in_reverse_order()
    {
        using var rig = new MacroPlayerRig(realWaitCapMs: 10_000);
        var m = TestMacro.Of(new KeyDownStep(LShift), new KeyDownStep(A), new DelayStep(60_000), new KeyUpStep(A), new KeyTapStep(C));

        Assert.Equal(TriggerResult.Started, rig.Player.Trigger(m));
        MacroPlayerRig.WaitFor(() => rig.Log.All.Contains("wait 60000"));
        Assert.True(rig.Player.IsRunning(m.Id));
        rig.Player.StopAll();

        Assert.True(rig.Player.WaitForIdle(TimeSpan.FromSeconds(5)));
        Assert.Equal([Down(LShift), Down(A), Up(A), Up(LShift)], rig.Log.Input);
        Assert.Equal(MacroRunResult.Stopped, Assert.Single(rig.Finished).Result);
        Assert.False(rig.Player.IsRunning(m.Id));
    }

    [Fact]
    public void Stopping_during_a_held_tap_releases_the_tap_and_its_modifiers()
    {
        using var rig = new MacroPlayerRig(realWaitCapMs: 10_000);
        var m = TestMacro.Of(new KeyTapStep(A, MacroModifiers.Ctrl, HoldMs: 60_000));

        rig.Player.Trigger(m);
        MacroPlayerRig.WaitFor(() => rig.Log.All.Contains("wait 60000"));
        Assert.True(rig.Player.Stop(m.Id));

        Assert.True(rig.Player.WaitForIdle(TimeSpan.FromSeconds(5)));
        Assert.Equal([Down(LControl), Down(A), Up(A), Up(LControl)], rig.Log.Input);
    }

    [Fact]
    public void Toggle_loops_until_triggered_again()
    {
        using var rig = new MacroPlayerRig(realWaitCapMs: 5);
        var m = TestMacro.Of(new KeyTapStep(A), new DelayStep(5));
        m.Mode = PlaybackMode.Toggle;

        Assert.Equal(TriggerResult.Started, rig.Player.Trigger(m));
        MacroPlayerRig.WaitFor(() => rig.Log.Count(Down(A)) >= 3);
        Assert.Equal(TriggerResult.Stopped, rig.Player.Trigger(m));

        Assert.True(rig.Player.WaitForIdle(TimeSpan.FromSeconds(5)));
        Assert.False(rig.Player.IsRunning(m.Id));
        Assert.Equal(rig.Log.Count(Down(A)), rig.Log.Count(Up(A)));
        Assert.Equal(MacroRunResult.Stopped, Assert.Single(rig.Finished).Result);
    }

    [Fact]
    public void Retriggering_a_running_macro_is_ignored()
    {
        using var rig = new MacroPlayerRig(realWaitCapMs: 10_000);
        var m = TestMacro.Of(new DelayStep(60_000));
        m.Mode = PlaybackMode.RepeatCount;
        m.RepeatCount = 2;

        rig.Player.Trigger(m);
        MacroPlayerRig.WaitFor(() => rig.Log.All.Contains("wait 60000"));
        Assert.Equal(TriggerResult.Ignored, rig.Player.Trigger(m));
        rig.Player.StopAll();

        Assert.True(rig.Player.WaitForIdle(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, rig.Log.Count("wait 60000"));
    }

    [Fact]
    public void StopAll_stops_every_running_macro()
    {
        using var rig = new MacroPlayerRig(realWaitCapMs: 10_000);
        var a = TestMacro.Of(new KeyDownStep(A), new DelayStep(60_000));
        var b = TestMacro.Of(new KeyDownStep(C), new DelayStep(60_001));

        rig.Player.Trigger(a);
        rig.Player.Trigger(b);
        MacroPlayerRig.WaitFor(() => rig.Log.All.Contains("wait 60000") && rig.Log.All.Contains("wait 60001"));
        Assert.Equal(2, rig.Player.RunningCount);
        rig.Player.StopAll();

        Assert.True(rig.Player.WaitForIdle(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, rig.Player.RunningCount);
        Assert.Contains(Up(A), rig.Log.All);
        Assert.Contains(Up(C), rig.Log.All);
    }

    [Fact]
    public void Waits_for_held_modifiers_to_be_released_before_injecting()
    {
        using var rig = new MacroPlayerRig();
        rig.Keys.CtrlHeldForChecks = 3;

        rig.PlayToEnd(TestMacro.Of(new KeyTapStep(A)));

        Assert.Equal(["wait 10", "wait 10", "wait 10", Down(A), Up(A)], rig.Log.All);
    }

    [Fact]
    public void Waits_for_a_held_Win_key_too()
    {
        using var rig = new MacroPlayerRig();
        rig.Keys.HeldVk = LWin;
        rig.Keys.CtrlHeldForChecks = 2;

        rig.PlayToEnd(TestMacro.Of(new KeyTapStep(A)));

        Assert.Equal(["wait 10", "wait 10", Down(A), Up(A)], rig.Log.All);
    }

    [Fact]
    public void Modifier_wait_gives_up_after_one_second()
    {
        using var rig = new MacroPlayerRig();
        rig.Keys.CtrlHeldForChecks = int.MaxValue;

        rig.PlayToEnd(TestMacro.Of(new KeyTapStep(A)));

        Assert.Equal(100, rig.Log.Count("wait 10"));
        Assert.Equal([Down(A), Up(A)], rig.Log.Input);
    }

    [Fact]
    public void A_failing_step_stops_the_macro_releases_keys_and_reports_the_error()
    {
        using var rig = new MacroPlayerRig();
        rig.Shell.LaunchThrows = new MacroStepException("Program not found: x.exe");
        Exception? failure = null;
        rig.Player.Failed += (_, e) => failure = e;

        rig.PlayToEnd(TestMacro.Of(new KeyDownStep(LControl), new LaunchProgramStep("x.exe"), new KeyTapStep(A)));

        Assert.IsType<MacroStepException>(failure);
        Assert.Equal([Down(LControl), "launch x.exe||", Up(LControl)], rig.Log.All);
        Assert.Equal(MacroRunResult.Failed, Assert.Single(rig.Finished).Result);
    }

    [Fact]
    public void Invalid_key_codes_fail_instead_of_being_sent()
    {
        using var rig = new MacroPlayerRig();
        rig.PlayToEnd(TestMacro.Of(new KeyTapStep(0), new KeyTapStep(A)));

        Assert.Empty(rig.Log.All);
        Assert.Equal(MacroRunResult.Failed, Assert.Single(rig.Finished).Result);
    }

    [Fact]
    public void Started_is_raised_and_steps_are_snapshotted_at_trigger_time()
    {
        using var rig = new MacroPlayerRig();
        var started = new List<Macro>();
        rig.Player.Started += started.Add;
        var m = TestMacro.Of(new KeyTapStep(A));

        rig.Player.Trigger(m);
        m.Steps.Add(new KeyTapStep(C));
        Assert.True(rig.Player.WaitForIdle(TimeSpan.FromSeconds(5)));

        Assert.Same(m, Assert.Single(started));
        Assert.Equal([Down(A), Up(A)], rig.Log.All);
    }

    [Fact]
    public void Disposed_player_refuses_new_triggers()
    {
        var rig = new MacroPlayerRig();
        rig.Dispose();
        Assert.Throws<ObjectDisposedException>(() => rig.Player.Trigger(TestMacro.Of()));
    }
}

// ======================================================================== OS adapters (no input is ever injected)

public class MacroShellTests
{
    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/path?q=1#x", true)]
    [InlineData("  https://example.com  ", true)]
    [InlineData("HTTPS://EXAMPLE.COM", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData(@"C:\Windows\System32\calc.exe", false)]
    [InlineData(@"\\server\share\x.exe", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("mailto:someone@example.com", false)]
    [InlineData("ms-settings:display", false)]
    [InlineData("example.com", false)]
    [InlineData("https://", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_http_and_https_urls_are_allowed(string? url, bool allowed) => Assert.Equal(allowed, ShellLauncher.IsAllowedUrl(url));

    [Fact]
    public void Invalid_targets_are_rejected_before_anything_starts()
    {
        var started = new List<ProcessStartInfo>();
        var shell = new ShellLauncher(started.Add);

        Assert.Throws<MacroStepException>(() => shell.OpenUrl("javascript:alert(1)"));
        Assert.Throws<MacroStepException>(() => shell.OpenUrl("file:///C:/Windows/System32/calc.exe"));
        Assert.Throws<MacroStepException>(() => shell.Launch(@"C:\nonexistent-darkmount\app.exe", null, null));
        Assert.Throws<MacroStepException>(() => shell.Launch(@"relative\app.exe", null, null));
        Assert.Throws<MacroStepException>(() => shell.Launch("", null, null));
        Assert.Throws<MacroStepException>(() => shell.Launch("cmd.exe", null, @"C:\nonexistent-darkmount"));
        Assert.Throws<MacroStepException>(() => shell.OpenFolder(@"C:\nonexistent-darkmount"));
        Assert.Empty(started);
    }

    [Fact]
    public void Valid_targets_are_started_through_the_shell()
    {
        var started = new List<ProcessStartInfo>();
        var shell = new ShellLauncher(started.Add);
        var temp = Path.GetTempPath();

        shell.OpenUrl(" https://example.com/a b ");
        shell.Launch("cmd", "/c echo hi", null);
        shell.Launch("%SystemRoot%\\System32\\cmd.exe", null, temp);
        shell.OpenFolder(temp);

        Assert.All(started, p => Assert.True(p.UseShellExecute));
        Assert.Equal("https://example.com/a%20b", started[0].FileName);
        Assert.EndsWith(@"\cmd.exe", started[1].FileName, StringComparison.OrdinalIgnoreCase);
        Assert.True(Path.IsPathFullyQualified(started[1].FileName));
        Assert.Equal("/c echo hi", started[1].Arguments);
        Assert.Equal(Path.GetDirectoryName(started[1].FileName), started[1].WorkingDirectory);
        Assert.True(File.Exists(started[2].FileName));
        Assert.Equal(temp, started[2].WorkingDirectory);
        Assert.Equal(temp, started[3].FileName);
    }

    [Fact]
    public void Programs_resolve_from_full_paths_or_the_PATH_only()
    {
        Assert.NotNull(ShellLauncher.ResolveProgram("cmd"));
        Assert.NotNull(ShellLauncher.ResolveProgram("\"cmd.exe\""));
        Assert.Null(ShellLauncher.ResolveProgram(@"C:\nonexistent-darkmount\app.exe"));
        Assert.Null(ShellLauncher.ResolveProgram(@"..\cmd.exe"));
        Assert.Null(ShellLauncher.ResolveProgram("   "));
    }

    [Fact]
    public void Precise_timer_waits_and_can_be_cancelled()
    {
        var timer = new PreciseTimer();
        using var cts = new CancellationTokenSource();

        var sw = Stopwatch.StartNew();
        Assert.True(timer.Wait(30, cts.Token));
        Assert.InRange(sw.ElapsedMilliseconds, 25, 1000);

        cts.CancelAfter(20);
        sw.Restart();
        Assert.False(timer.Wait(10_000, cts.Token));
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }

    [Fact]
    public void Native_input_struct_has_the_win32_size()
        => Assert.Equal(IntPtr.Size == 8 ? 40 : 28, SendInputSink.NativeInputSize);
}

public class MacroHotkeysTests
{
    const uint ModAlt = 0x1, ModCtrl = 0x2, ModShift = 0x4, ModWin = 0x8, NoRepeat = 0x4000;

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public MacroHotkeysTests() => Log.Enabled = false;

    [Fact]
    public void Update_registers_enabled_macros_and_reports_conflicts()
    {
        var api = new MacroFakeHotkeyApi { TakenByOtherPrograms = { (uint)TriggerKey.F20 } };
        using var hotkeys = new MacroHotkeys(api);
        var a = new Macro { Name = "A", Trigger = new(TriggerKey.F13, Ctrl: true) };
        var dup = new Macro { Name = "Dup", Trigger = new(TriggerKey.F13, Ctrl: true) };
        var c = new Macro { Name = "C", Trigger = new(TriggerKey.F14, Shift: true, Alt: true) };
        var off = new Macro { Name = "Off", Trigger = new(TriggerKey.F15), Enabled = false };
        var taken = new Macro { Name = "Taken", Trigger = new(TriggerKey.F20) };
        var bad = new Macro { Name = "Bad", Trigger = new((TriggerKey)0x41) };

        var failures = hotkeys.Update([a, dup, c, off, taken, bad]);

        Assert.Equal(2, api.Registered.Count);
        Assert.Contains((ModCtrl | NoRepeat, 0x7Cu), api.Registered.Values);
        Assert.Contains((ModShift | ModAlt | NoRepeat, 0x7Du), api.Registered.Values);
        Assert.Equal(["Dup", "Taken", "Bad"], failures.Select(f => f.Macro.Name));
        Assert.Contains("\"A\"", failures[0].Reason);
        Assert.Contains("another program", failures[1].Reason);
        Assert.Contains("need Ctrl or Win", failures[2].Reason);
        Assert.Same(failures, hotkeys.Failures);
    }

    [Fact]
    public void Any_keyboard_combinations_register_with_their_virtual_key()
    {
        var api = new MacroFakeHotkeyApi { TakenByOtherPrograms = { (uint)TriggerKey.L } };
        using var hotkeys = new MacroHotkeys(api);
        var k = new Macro { Name = "K", Trigger = new(TriggerKey.K, Ctrl: true, Shift: true) };
        var num = new Macro { Name = "Num", Trigger = new(TriggerKey.NumPad1, Win: true) };
        var taken = new Macro { Name = "Taken", Trigger = new(TriggerKey.L, Ctrl: true) };

        var failures = hotkeys.Update([k, num, taken]);

        Assert.Contains((ModCtrl | ModShift | NoRepeat, 0x4Bu), api.Registered.Values);
        Assert.Contains((ModWin | NoRepeat, 0x61u), api.Registered.Values);
        var failure = Assert.Single(failures);
        Assert.Equal("Ctrl+L is already taken by another program.", failure.Reason);
    }

    [Fact]
    public void Hotkey_message_raises_Triggered_with_the_matching_macro()
    {
        var api = new MacroFakeHotkeyApi();
        using var hotkeys = new MacroHotkeys(api);
        var a = new Macro { Trigger = new(TriggerKey.F13) };
        var b = new Macro { Trigger = new(TriggerKey.F14) };
        hotkeys.Update([a, b]);
        var got = new List<Macro>();
        hotkeys.Triggered += got.Add;

        var idOfB = api.Registered.Single(kv => kv.Value.Vk == (uint)TriggerKey.F14).Key;
        SendMessage(hotkeys.Handle, 0x0312, idOfB, IntPtr.Zero);
        SendMessage(hotkeys.Handle, 0x0312, 0x7777, IntPtr.Zero);

        Assert.Same(b, Assert.Single(got));
    }

    [Fact]
    public void Update_replaces_registrations_and_Dispose_removes_them()
    {
        var api = new MacroFakeHotkeyApi();
        var hotkeys = new MacroHotkeys(api);
        var a = new Macro { Trigger = new(TriggerKey.F13) };
        var b = new Macro { Trigger = new(TriggerKey.F14) };

        hotkeys.Update([a, b]);
        Assert.Equal(2, api.Registered.Count);
        hotkeys.Update([b]);
        Assert.Equal((uint)TriggerKey.F14, Assert.Single(api.Registered.Values).Vk);
        Assert.Equal(1, hotkeys.RegisteredCount);

        hotkeys.Dispose();
        Assert.Empty(api.Registered);
    }

    [Theory]
    [InlineData(false, false, false, 0u)]
    [InlineData(true, false, false, ModCtrl)]
    [InlineData(false, true, false, ModShift)]
    [InlineData(false, false, true, ModAlt)]
    [InlineData(true, true, true, ModCtrl | ModShift | ModAlt)]
    public void Modifier_flags_match_RegisterHotKey(bool ctrl, bool shift, bool alt, uint expected)
        => Assert.Equal(expected | NoRepeat, MacroHotkeys.HotkeyModifiers(new MacroTrigger(TriggerKey.F13, ctrl, shift, alt)));

    [Fact]
    public void Win_maps_to_MOD_WIN()
    {
        Assert.Equal(ModWin | NoRepeat, MacroHotkeys.HotkeyModifiers(new MacroTrigger(TriggerKey.K, Win: true)));
        Assert.Equal(ModWin | ModShift | NoRepeat, MacroHotkeys.HotkeyModifiers(new MacroTrigger(TriggerKey.F5, Shift: true, Win: true)));
    }
}
