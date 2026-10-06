using Darkmount.App.Macros;
using Darkmount.Keyboard;

namespace Darkmount.App.IoCenter;

/// <summary>
/// Makes IO Center's "open program / folder / website" keys work without IO Center: each becomes an OverMount macro
/// with an F13–F24 trigger, and the key is bound to send that trigger. Identical launchers share one macro.
/// </summary>
public static class IoCenterMacros
{
    /// <summary>Adds the needed macros to <paramref name="macros"/> and the trigger bindings to the profile; returns notes.</summary>
    public static List<string> Attach(IoCenterImportResult result, List<Macro> macros)
    {
        var notes = new List<string>();
        var bindings = result.Profile.Keyboard.Bindings ??= [];
        foreach (var launcher in result.Launchers)
        {
            // Only a macro a Dark Mount key can send (F13–F24): any-keyboard combinations can't be bound to a key.
            var macro = macros.FirstOrDefault(m => m.Steps is [var only] && only == launcher.Step && m.Trigger.UsesF13ToF24);
            if (macro is null)
            {
                if (FreeTrigger(macros) is not { } trigger)
                {
                    notes.Add($"{launcher.KeyLabel}: no free macro trigger left (F13–F24 combinations are all used).");
                    continue;
                }
                macro = new Macro { Name = $"Open {Describe(launcher.Step)}", Trigger = trigger, Steps = [launcher.Step] };
                macros.Add(macro);
            }
            bindings.RemoveAll(b => b.KeyId == launcher.KeyId && b.Layer == launcher.Layer);
            bindings.Add(new KeyBinding(launcher.KeyId, launcher.Layer, TriggerAction(macro.Trigger)));
            notes.Add($"{launcher.KeyLabel} opens {Describe(launcher.Step)} through the macro \"{macro.Name}\" ({macro.Trigger}).");
        }
        return notes;
    }

    /// <summary>
    /// What the keyboard sends for a trigger: an F13–F24 key, with Ctrl/Shift/Alt/Win when the trigger has them.
    /// Other triggers (e.g. Ctrl+Shift+K) are pressed on any keyboard and can't be bound.
    /// </summary>
    public static BindingAction TriggerAction(MacroTrigger t)
    {
        if (!t.UsesF13ToF24) throw new ArgumentException($"Only F13–F24 triggers can be bound to a key, not {t}.", nameof(t));
        byte usage = HidUsage.FKey((int)t.Key - (int)TriggerKey.F13 + 13);
        var mods = (t.Ctrl ? KeyModifiers.LeftCtrl : 0) | (t.Shift ? KeyModifiers.LeftShift : 0) | (t.Alt ? KeyModifiers.LeftAlt : 0)
                 | (t.Win ? KeyModifiers.LeftWin : 0);
        return mods == KeyModifiers.None ? new BindingAction.FKey(usage) : new BindingAction.StandardKey(mods, usage);
    }

    /// <summary>F13…F24 first, then the same keys with Ctrl, Shift, Alt and their combinations.</summary>
    static MacroTrigger? FreeTrigger(List<Macro> macros)
    {
        var used = macros.Select(m => m.Trigger).ToHashSet();
        foreach (var (ctrl, shift, alt) in new[] { (false, false, false), (true, false, false), (false, true, false), (false, false, true),
                     (true, true, false), (true, false, true), (false, true, true), (true, true, true) })
            foreach (var key in MacroTrigger.AllKeys.Where(MacroTrigger.IsF13ToF24))
                if (new MacroTrigger(key, ctrl, shift, alt) is var t && !used.Contains(t)) return t;
        return null;
    }

    static string Describe(MacroStep step) => step switch
    {
        LaunchProgramStep p => Path.GetFileNameWithoutExtension(p.Path),
        OpenFolderStep f => Path.GetFileName(f.Path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : f.Path,
        OpenUrlStep u => Uri.TryCreate(u.Url, UriKind.Absolute, out var uri) ? uri.Host : u.Url,
        _ => "item",
    };
}
