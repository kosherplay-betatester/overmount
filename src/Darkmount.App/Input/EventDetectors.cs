using Darkmount.Keyboard.Lamps;

namespace Darkmount.App.Input;

/// <summary>
/// Finds the beats in music: a sudden rise of the bass above its recent average (then a short pause, so one kick is one
/// beat). Fed once per lighting frame; keeps the last few seconds of beats for the effects.
/// </summary>
public sealed class BeatDetector
{
    const double Refractory = 0.24, Keep = 3;
    readonly Queue<double> _beats = new();
    double _average, _lastBeat = double.NegativeInfinity;
    bool _primed;

    public IReadOnlyList<double> Beats => [.. _beats];

    public void Feed(double seconds, IReadOnlyList<double> bands, double level)
    {
        double bass = bands.Count >= 3 ? (bands[0] + bands[1] + bands[2]) / 3 : level;
        while (_beats.Count > 0 && seconds - _beats.Peek() > Keep) _beats.Dequeue();
        if (!_primed) { _average = bass; _primed = true; return; }
        bool beat = bass > _average * 1.45 + 0.06 && seconds - _lastBeat > Refractory;
        _average += (bass - _average) * 0.08;
        if (!beat) return;
        _lastBeat = seconds;
        _beats.Enqueue(seconds);
        while (_beats.Count > 12) _beats.Dequeue();
    }
}

/// <summary>
/// Finds screen flashes (explosions, muzzle flashes, lightning): the screen suddenly much brighter than a moment ago.
/// Fed with each screen colour sample (~12 a second).
/// </summary>
public sealed class FlashDetector
{
    const double Refractory = 0.25, Keep = 2;
    readonly Queue<ScreenFlash> _flashes = new();
    double _average, _lastFlash = double.NegativeInfinity;
    bool _primed;

    public IReadOnlyList<ScreenFlash> Flashes => [.. _flashes];

    public void Feed(double seconds, IReadOnlyList<LampColor> grid)
    {
        while (_flashes.Count > 0 && seconds - _flashes.Peek().At > Keep) _flashes.Dequeue();
        if (grid.Count == 0) return;
        double r = 0, g = 0, b = 0;
        foreach (var c in grid) { r += c.R; g += c.G; b += c.B; }
        r /= grid.Count; g /= grid.Count; b /= grid.Count;
        double lum = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
        if (!_primed) { _average = lum; _primed = true; return; }
        double jump = lum - _average;
        _average += (lum - _average) * 0.15;
        if (jump < 0.12 || lum < 0.25 || seconds - _lastFlash < Refractory) return;
        _lastFlash = seconds;
        _flashes.Enqueue(new ScreenFlash(seconds, new LampColor((byte)r, (byte)g, (byte)b), Math.Min(1, jump * 3)));
        while (_flashes.Count > 8) _flashes.Dequeue();
    }
}
