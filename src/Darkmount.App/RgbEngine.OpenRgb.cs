using System.Text.RegularExpressions;
using Darkmount.App.LightDevices;
using Darkmount.Keyboard.Lamps.OpenRgb;

namespace Darkmount.App;

/// <summary>Keyboards and laptops reached through OpenRGB (engine thread; the network I/O is on the link's own thread).</summary>
public sealed partial class RgbEngine
{
    OpenRgbLink? _link;
    int _linkGeneration = -1;

    /// <summary>
    /// Each OpenRGB device's own mode, from when OverMount first took it, kept until it is handed back: after OpenRGB
    /// re-lists its devices they report the direct mode OverMount switched them to.
    /// </summary>
    readonly Dictionary<string, (int Mode, byte[] Raw)> _openRgbOwnModes = [];

    /// <summary>
    /// Lights OpenRGB's keyboards, keypads and laptops — except devices OverMount already lights another way (be quiet!
    /// keyboards, and keyboards with the Windows standard, which is faster and needs no extra app). Never blocks: the
    /// link connects, lists and sends on its own thread.
    /// </summary>
    void UpdateOpenRgb(AppSettings s, HashSet<string> off)
    {
        if (!s.OpenRgbEnabled)
        {
            CloseOpenRgb();
            return;
        }
        _link ??= new OpenRgbLink();
        int generation = _link.Generation;
        if (generation != _linkGeneration)
        {
            // Connected, disconnected or the list changed: indices may have moved, so stop drawing without a hand-back.
            DropOpenRgbOutputs(handBack: false, _link.Connected ? "Re-reading OpenRGB's devices" : "OpenRGB isn't running");
            _linkGeneration = generation;
            var present = _link.Controllers.Where(c => c.IsKeyboardLike).Select(OpenRgbOutput.IdOf).ToHashSet();
            lock (_info)
                foreach (var gone in _info.Values.Where(i => i.Via == LightVia.OpenRgb && !present.Contains(i.Id)).Select(i => i.Id).ToList())
                    _info.Remove(gone);
        }
        foreach (var c in _link.Controllers.Where(c => c.IsKeyboardLike))
        {
            string id = OpenRgbOutput.IdOf(c);
            if (_outputs.Any(o => o.Id == id) || LitAnotherWay(c)) continue;
            if (off.Contains(id)) { SetInfo(id, c.Name, LightVia.OpenRgb, false, "Off (switched off here)"); continue; }
            if (c.LedCount == 0 || !c.Modes.Any(m => m.HasPerLedColor))
            {
                SetInfo(id, c.Name, LightVia.OpenRgb, false, "OpenRGB can't set its colours one by one");
                continue;
            }
            if (!_openRgbOwnModes.ContainsKey(id) && OpenRgbOutput.OwnMode(c) is { } first) _openRgbOwnModes[id] = first;
            var output = new OpenRgbOutput(_link, c, generation, _openRgbOwnModes.TryGetValue(id, out var own) ? own : null);
            Add(output);
            Log.Write($"RGB engine: {c.Name} via OpenRGB: {c.LedCount} LEDs, {output.LampOfKey.Count} keys");
        }
    }

    /// <summary>A be quiet! keyboard, or one already lit through the Windows standard (same USB ids).</summary>
    bool LitAnotherWay(OpenRgbController c)
    {
        var m = UsbIds().Match(c.Location);
        if (!m.Success) return false;
        if (string.Equals(m.Groups[1].Value, "373F", StringComparison.OrdinalIgnoreCase)) return true;
        string std = $"std:{m.Groups[1].Value.ToUpperInvariant()}:{m.Groups[2].Value.ToUpperInvariant()}";
        return _outputs.Any(o => string.Equals(o.Id, std, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})")]
    private static partial Regex UsbIds();

    void DropOpenRgbOutputs(bool handBack, string state)
    {
        foreach (var output in _outputs.Where(o => o.Via == LightVia.OpenRgb).ToList()) Drop(output, handBack, state);
    }

    /// <summary>Hands OpenRGB's devices back and closes the link (it sends those last hand-backs before it ends).</summary>
    void CloseOpenRgb()
    {
        DropOpenRgbOutputs(handBack: true, "Off");
        _link?.Dispose();
        _link = null;
        _linkGeneration = -1;
    }
}
