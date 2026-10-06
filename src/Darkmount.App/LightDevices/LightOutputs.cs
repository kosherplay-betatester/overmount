using Darkmount.Keyboard.Lamps;

namespace Darkmount.App.LightDevices;

/// <summary>How OverMount reaches a lighting device.</summary>
public enum LightVia { BeQuiet, WindowsStandard, OpenRgb }

/// <summary>What the Lighting page shows for one device the engine found.</summary>
/// <param name="Id">Stable across restarts (used to remember devices the user switched off).</param>
/// <param name="State">"Lit by OverMount", "Off (you switched it off)", "Windows Dynamic Lighting controls it", …</param>
public sealed record LightDeviceInfo(string Id, string Name, LightVia Via, bool Active, string State)
{
    public string ViaText => Via switch
    {
        LightVia.BeQuiet => "be quiet!",
        LightVia.WindowsStandard => "Windows lighting standard",
        _ => "OpenRGB",
    };
}

/// <summary>
/// One device the RGB engine draws on. Lamp ids and <see cref="Layout"/> belong to the device; frames are keyed by
/// lamp id. Used from the engine thread only.
/// </summary>
public interface ILightOutput : IDisposable
{
    string Id { get; }
    string Name { get; }
    LightVia Via { get; }

    /// <summary>Where each lamp sits (0..1) and which Dark Mount key id it lights, for effects and overlays.</summary>
    IReadOnlyList<LampPoint> Layout { get; }

    /// <summary>Dark Mount key id → lamp id (keys the device lights).</summary>
    IReadOnlyDictionary<int, int> LampOfKey { get; }

    /// <summary>Fastest the device should be updated (frames per second).</summary>
    double MaxFps { get; }

    /// <summary>Shows a frame: takes the lighting over on the first call, then sends only lamps that changed.</summary>
    void Send(IReadOnlyDictionary<int, LampColor> frame);

    /// <summary>Gives the lighting back to the device's own effect; the next <see cref="Send"/> takes it again.</summary>
    void HandBack();
}

/// <summary>A LampArray (Windows Dynamic Lighting standard) device: a be quiet! keyboard or any other maker's.</summary>
public sealed class LampArrayOutput : ILightOutput
{
    readonly LampArrayDevice _device;
    readonly Dictionary<int, LampColor> _sent = [];
    bool _hostControl;

    public LampArrayOutput(LampArrayDevice device, string id, string name, LightVia via, IReadOnlyList<LampPoint> layout)
    {
        _device = device;
        Id = id;
        Name = name;
        Via = via;
        Layout = layout;
        LampOfKey = layout.Where(p => p.KeyId > 0).GroupBy(p => p.KeyId).ToDictionary(g => g.Key, g => g.First().LampId);
        // The device says how often it accepts updates; LampArrayDevice also waits for that before each one.
        int min = device.Attributes.MinUpdateIntervalMicroseconds;
        MaxFps = min > 0 ? Math.Clamp(1_000_000.0 / min, 5, 60) : 30;
    }

    public string Id { get; }
    public string Name { get; }
    public LightVia Via { get; }
    public IReadOnlyList<LampPoint> Layout { get; }
    public IReadOnlyDictionary<int, int> LampOfKey { get; }
    public double MaxFps { get; }
    public string? DevicePath => _device.DevicePath;

    /// <summary>
    /// Sends only lamps whose colour changed; runs of ≥ 3 consecutive lamp ids with one colour go as a single range
    /// report (a whole-keyboard colour is one report, ~5 ms).
    /// </summary>
    public void Send(IReadOnlyDictionary<int, LampColor> frame)
    {
        if (!_hostControl)
        {
            _device.SetAutonomousMode(false);
            _hostControl = true;
            _sent.Clear();
        }
        var changed = frame.Where(kv => !_sent.TryGetValue(kv.Key, out var old) || old != kv.Value).OrderBy(kv => kv.Key).ToList();
        if (changed.Count == 0) return;

        var ranges = new List<(int, int, LampColor)>();
        var single = new Dictionary<int, LampColor>();
        for (int i = 0; i < changed.Count;)
        {
            int j = i;
            while (j + 1 < changed.Count && changed[j + 1].Key == changed[j].Key + 1 && changed[j + 1].Value == changed[i].Value) j++;
            if (j - i >= 2) ranges.Add((changed[i].Key, changed[j].Key, changed[i].Value));
            else for (int k = i; k <= j; k++) single[changed[k].Key] = changed[k].Value;
            i = j + 1;
        }
        _device.SetFrame(single, ranges);
        foreach (var (id, c) in changed) _sent[id] = c;
    }

    public void HandBack()
    {
        try { if (_hostControl) _device.SetAutonomousMode(true); }
        catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException) { }
        _hostControl = false;
        _sent.Clear();
    }

    public void Dispose()
    {
        HandBack();
        _device.Dispose();
    }
}
