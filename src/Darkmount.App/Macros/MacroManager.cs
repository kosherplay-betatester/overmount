namespace Darkmount.App.Macros;

/// <summary>
/// Owns the macro list, the hotkey triggers (F13–F24, or any key with Ctrl/Alt/Shift/Win) and the player. Create on
/// the UI thread (hotkeys need a window handle). Dark Mount keys are bound to F13–F24 triggers from the Macros or Keys page.
/// </summary>
public sealed class MacroManager : IDisposable
{
    readonly MacroPlayer _player = MacroPlayer.CreateDefault();
    readonly MacroHotkeys _hotkeys = new();
    readonly string _path;

    public List<Macro> Macros { get; private set; }
    public IReadOnlyList<HotkeyFailure> Failures { get; private set; } = [];

    public event Action<Macro, string>? Problem;

    public MacroManager(string? path = null)
    {
        _path = path ?? MacroStore.DefaultPath;
        Macros = MacroStore.Load(_path);
        _hotkeys.Triggered += m => _player.Trigger(m);
        _player.Failed += (m, e) => { Log.Write($"Macro '{m.Name}' failed: {e.Message}"); Problem?.Invoke(m, e.Message); };
        Register();
    }

    /// <summary>Saves the macros and re-registers their triggers; returns the triggers that could not be registered.</summary>
    public IReadOnlyList<HotkeyFailure> Save(IEnumerable<Macro> macros)
    {
        Macros = macros.Select(m => m.Clone()).ToList();
        MacroStore.Save(_path, Macros);
        return Register();
    }

    IReadOnlyList<HotkeyFailure> Register()
    {
        Failures = _hotkeys.Update(Macros.Where(m => m.Enabled));
        foreach (var f in Failures) Log.Write($"Macro trigger for '{f.Macro.Name}' not registered: {f.Reason}");
        return Failures;
    }

    /// <summary>Disables all triggers (while recording, so trigger keys don't fire macros).</summary>
    public void SuspendTriggers() => _hotkeys.Update([]);

    public void ResumeTriggers() => Register();

    public void Test(Macro macro) => _player.Trigger(macro);

    public void StopAll() => _player.StopAll();

    /// <summary>The first F13–F24 trigger (without modifiers) no macro uses yet.</summary>
    public MacroTrigger FreeTrigger() => FreeTrigger(Macros);

    /// <inheritdoc cref="FreeTrigger()"/>
    public static MacroTrigger FreeTrigger(IReadOnlyCollection<Macro> macros) =>
        MacroTrigger.AllKeys.Where(MacroTrigger.IsF13ToF24).Select(k => new MacroTrigger(k))
            .FirstOrDefault(t => macros.All(m => m.Trigger != t)) ?? new MacroTrigger(TriggerKey.F24, Ctrl: true);

    public void Dispose()
    {
        _hotkeys.Dispose();
        _player.Dispose();
    }
}
