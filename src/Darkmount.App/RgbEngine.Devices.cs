using Darkmount.App.LightDevices;
using Darkmount.Keyboard.Lamps;

namespace Darkmount.App;

/// <summary>Finding, opening and dropping the devices the engine draws on (engine thread only).</summary>
public sealed partial class RgbEngine
{
    readonly Dictionary<string, LightDeviceInfo> _info = [];
    readonly HashSet<string> _skippedPaths = new(StringComparer.OrdinalIgnoreCase); // other LampArrays that aren't keyboards
    int _rescan = 1; // 1 = look for devices again now (set from HidSharp's thread too)
    long _nextBeQuietTry, _nextScan;
    Darkmount.Keyboard.NumpadSide _numpadSide;
    string _noDeviceStatus = "No RGB keyboard found";
    string _offSeen = "";

    /// <summary>The be quiet! keyboard's output, published for the UI thread (the studio preview reads its layout).</summary>
    volatile LampArrayOutput? _beQuiet;

    /// <summary>Every lighting device found, for the Lighting page (thread-safe snapshot).</summary>
    public IReadOnlyList<LightDeviceInfo> Devices
    {
        get { lock (_info) return [.. _info.Values.OrderBy(i => i.Via).ThenBy(i => i.Name)]; }
    }

    LampArrayOutput? BeQuiet => _beQuiet;

    /// <summary>A device came or went: look again right away (also for the be quiet! keyboard).</summary>
    void OnDevicesChanged(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _nextBeQuietTry, 0);
        RequestRescan();
    }

    void SetInfo(string id, string name, LightVia via, bool active, string state)
    {
        lock (_info) _info[id] = new LightDeviceInfo(id, name, via, active, state);
    }

    void RemoveInfo(string id)
    {
        lock (_info) _info.Remove(id);
    }

    /// <summary>Opens newly found devices and closes those that went away, were switched off, or are busy.</summary>
    void UpdateOutputs(AppSettings s, bool ioCenter)
    {
        var off = new HashSet<string>(s.LightingDevicesOff ?? [], StringComparer.OrdinalIgnoreCase);
        long now = Environment.TickCount64;
        string offSeen = string.Join("|", off.Order(StringComparer.OrdinalIgnoreCase));
        bool rescan = Interlocked.Exchange(ref _rescan, 0) == 1;
        if (offSeen != _offSeen) { _offSeen = offSeen; rescan = true; } // a device switched back on comes back now

        // Devices the user switched off are handed back at once.
        foreach (var output in _outputs.Where(o => off.Contains(o.Id)).ToList())
            Drop(output, handBack: true, "Off (switched off here)");

        UpdateBeQuiet(s, ioCenter, off, now, rescan);
        if (rescan || now >= _nextScan)
        {
            _nextScan = now + 15_000; // also catches devices whose arrival Windows didn't announce
            UpdateStandardDevices(off);
        }
        UpdateOpenRgb(s, off);
    }

    void UpdateBeQuiet(AppSettings s, bool ioCenter, HashSet<string> off, long now, bool rescan)
    {
        var current = BeQuiet;
        if (current is not null)
        {
            if (ioCenter) { Drop(current, handBack: true, "Paused: IO Center is running"); return; }
            if (s.NumpadSide != _numpadSide) Drop(current, handBack: false, "Re-reading the layout"); // re-lay out the keys
            else return;
        }
        if (ioCenter || now < Interlocked.Read(ref _nextBeQuietTry)) return;
        Interlocked.Exchange(ref _nextBeQuietTry, now + 2000);
        LampArrayDevice? device = null;
        try
        {
            device = LampArrayDevice.Open();
            if (device is null) { _noDeviceStatus = "No RGB keyboard found"; return; }
            const string id = "bequiet"; // one be quiet! keyboard at a time
            string name = "be quiet! keyboard";
            if (off.Contains(id))
            {
                SetInfo(id, name, LightVia.BeQuiet, false, "Off (switched off here)");
                device.Dispose();
                return;
            }
            if (device.DevicePath is { } path && DynamicLighting.Read(path).WindowsMayDrive)
            {
                _noDeviceStatus = "Windows Dynamic Lighting controls the keyboard (see Home → Setup check)";
                SetInfo(id, name, LightVia.BeQuiet, false, "Windows Dynamic Lighting controls it (turn it off for this keyboard)");
                device.Dispose();
                return;
            }
            var lamps = device.Lamps;
            _numpadSide = s.NumpadSide;
            var layout = RgbEffects.Layout(lamps, device.Map, _numpadSide);
            var output = new LampArrayOutput(device, id, name, LightVia.BeQuiet, layout);
            device = null;
            Add(output);
            Log.Write($"RGB engine: {lamps.Count} lamps, {output.LampOfKey.Count} keys");
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException
                                      or InvalidDataException or ObjectDisposedException)
        {
            Log.Write($"RGB engine: be quiet! keyboard: {e.Message}");
            _noDeviceStatus = "Keyboard lighting not available";
        }
        finally { device?.Dispose(); }
    }

    /// <summary>Keyboards and laptops with the Windows Dynamic Lighting standard (HID LampArray), any maker.</summary>
    void UpdateStandardDevices(HashSet<string> off)
    {
        IReadOnlyList<(HidSharp.HidDevice Device, byte[] Descriptor)> found;
        try { found = HidSharpLampArrayTransport.FindOthers(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Write($"RGB engine: looking for lighting devices failed: {e.Message}");
            return;
        }
        var open = _outputs.OfType<LampArrayOutput>().Where(o => o.Via == LightVia.WindowsStandard)
            .Select(o => o.DevicePath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (hid, descriptor) in found)
        {
            if (open.Contains(hid.DevicePath) || _skippedPaths.Contains(hid.DevicePath)) continue;
            string id = $"std:{hid.VendorID:X4}:{hid.ProductID:X4}";
            string name = DeviceName(hid);
            if (off.Contains(id)) { SetInfo(id, name, LightVia.WindowsStandard, false, "Off (switched off here)"); continue; }
            LampArrayDevice? device = null;
            try
            {
                device = LampArrayDevice.Open(hid, descriptor);
                if (device is null) continue;
                if (device.Attributes.Kind != LampArrayKind.Keyboard)
                {
                    _skippedPaths.Add(hid.DevicePath); // mice, light strips…: not ours to light
                    continue;
                }
                if (DynamicLighting.Read(hid.DevicePath).WindowsMayDrive)
                {
                    SetInfo(id, name, LightVia.WindowsStandard, false, "Windows Dynamic Lighting controls it (turn it off for this device to use OverMount)");
                    continue;
                }
                var layout = RgbEffects.LayoutFromPositions(device.Lamps, device.Map, keyboard: true);
                if (layout.Count == 0) continue;
                var output = new LampArrayOutput(device, id, name, LightVia.WindowsStandard, layout);
                device = null;
                Add(output);
                Log.Write($"RGB engine: {name} ({id}): {layout.Count} lamps, {output.LampOfKey.Count} keys");
            }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException
                                          or InvalidDataException or ObjectDisposedException)
            {
                Log.Write($"RGB engine: {name}: {e.Message}");
                SetInfo(id, name, LightVia.WindowsStandard, false, $"Can't be opened ({e.Message})");
            }
            finally { device?.Dispose(); }
        }
    }

    static string DeviceName(HidSharp.HidDevice hid)
    {
        string? product = null, maker = null;
        try { product = hid.GetProductName(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { }
        try { maker = hid.GetManufacturer(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { }
        product = string.IsNullOrWhiteSpace(product) ? null : product.Trim();
        maker = string.IsNullOrWhiteSpace(maker) ? null : maker.Trim();
        return product is null ? $"RGB keyboard {hid.VendorID:X4}:{hid.ProductID:X4}"
            : maker is not null && !product.Contains(maker, StringComparison.OrdinalIgnoreCase) ? $"{maker} {product}" : product;
    }

    void Add(ILightOutput output)
    {
        _outputs.Add(output);
        if (output is LampArrayOutput { Via: LightVia.BeQuiet } beQuiet) _beQuiet = beQuiet;
        SetInfo(output.Id, output.Name, output.Via, true, "Lit by OverMount");
    }

    /// <summary>Stops drawing on a device; <paramref name="handBack"/> gives its lighting back first.</summary>
    void Drop(ILightOutput output, bool handBack, string state)
    {
        _outputs.Remove(output);
        _lastSent.Remove(output);
        if (ReferenceEquals(output, _beQuiet))
        {
            _beQuiet = null;
            LastFrame = new Dictionary<int, LampColor>();
        }
        try
        {
            if (handBack)
            {
                output.HandBack();
                if (output.Via == LightVia.OpenRgb) _openRgbOwnModes.Remove(output.Id);
            }
        }
        catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException or InvalidOperationException) { }
        try { output.Dispose(); }
        catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException or InvalidOperationException) { }
        SetInfo(output.Id, output.Name, output.Via, false, state);
    }

    /// <summary>Hands every device back to its own effect but keeps listing them (lighting switched off).</summary>
    void HandBackAll()
    {
        foreach (var output in _outputs.ToList()) Drop(output, handBack: true, "Off (keyboard's own effect)");
        RequestRescan(); // when lighting comes back on, find everything again at once
        Fps = 0;
    }

    void RequestRescan() => Interlocked.Exchange(ref _rescan, 1);

    void CloseAll(bool handBack)
    {
        foreach (var output in _outputs.ToList()) Drop(output, handBack, "Off");
        CloseOpenRgb();
        RequestRescan(); // after an error, find everything again at once
        Fps = 0;
    }
}
