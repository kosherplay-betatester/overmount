using Darkmount.Keyboard.Lamps;
using Darkmount.Keyboard.Lamps.OpenRgb;

namespace Darkmount.App.LightDevices;

/// <summary>
/// A keyboard or laptop OpenRGB controls. Frames go to the <see cref="OpenRgbLink"/> thread, which switches the device
/// to OpenRGB's direct mode on the first one; <see cref="HandBack"/> restores the mode it had when OverMount found it.
/// Lamp ids are OpenRGB LED indices.
/// </summary>
public sealed class OpenRgbOutput : ILightOutput
{
    readonly OpenRgbLink _link;
    readonly OpenRgbController _controller;
    readonly int _generation;
    readonly (int Mode, byte[] Raw)? _original;
    LampColor[]? _sent;
    bool _taken;

    /// <param name="generation">The <see cref="OpenRgbLink.Generation"/> the controller was listed in.</param>
    /// <param name="original">The mode to restore on hand-back (default: the one the device reports now).</param>
    public OpenRgbOutput(OpenRgbLink link, OpenRgbController controller, int generation, (int Mode, byte[] Raw)? original = null)
    {
        _original = original ?? OwnMode(controller);
        _link = link;
        _controller = controller;
        _generation = generation;
        Id = IdOf(controller);
        Name = controller.Name;
        Layout = OpenRgbLayout.Build(controller);
        LampOfKey = Layout.Where(p => p.KeyId > 0).GroupBy(p => p.KeyId).ToDictionary(g => g.Key, g => g.First().LampId);
    }

    /// <summary>The mode a device reports as active (its own effect when OverMount hasn't touched it), if valid.</summary>
    public static (int Mode, byte[] Raw)? OwnMode(OpenRgbController c) =>
        c.ActiveMode >= 0 && c.ActiveMode < c.Modes.Count ? (c.ActiveMode, c.Modes[c.ActiveMode].RawBlock) : null;

    /// <summary>Stable id: OpenRGB's name and location for the device (its index can change between scans).</summary>
    public static string IdOf(OpenRgbController c) => $"openrgb:{c.Name}|{c.Location}";

    public string Id { get; }
    public string Name { get; }
    public LightVia Via => LightVia.OpenRgb;
    public IReadOnlyList<LampPoint> Layout { get; }
    public IReadOnlyDictionary<int, int> LampOfKey { get; }

    /// <summary>OpenRGB passes every update on to the hardware; laptop lighting in particular is slow to change.</summary>
    public double MaxFps => 25;

    /// <summary>Posts the frame (never blocks on the network); unchanged frames aren't posted.</summary>
    public void Send(IReadOnlyDictionary<int, LampColor> frame)
    {
        var colors = new LampColor[_controller.LedCount];
        foreach (var (led, color) in frame)
            if ((uint)led < (uint)colors.Length) colors[led] = color;
        if (_sent is not null && colors.AsSpan().SequenceEqual(_sent)) return;
        _link.Post(_controller.Index, _generation, colors);
        _sent = colors;
        _taken = true;
    }

    /// <summary>Puts back the mode the device had when OverMount found it (OpenRGB's own effect or profile).</summary>
    public void HandBack()
    {
        if (!_taken) return;
        _taken = false;
        _sent = null;
        if (_original is { } own) _link.PostHandBack(_controller.Index, _generation, own.Mode, own.Raw);
    }

    /// <summary>Nothing to release: the link is shared, and hand-back is explicit (it must not reach a moved index).</summary>
    public void Dispose() { }
}
