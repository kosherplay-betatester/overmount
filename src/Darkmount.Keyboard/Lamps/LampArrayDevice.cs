using System.Diagnostics;

namespace Darkmount.Keyboard.Lamps;

/// <summary>
/// The Dark Mount's standard HID LampArray (usage page 0x59, interface MI_03) — the protocol Windows Dynamic Lighting uses.
/// Opening it and reading <see cref="Attributes"/>/<see cref="Lamps"/> is read-only (GetFeature, plus the
/// LampAttributesRequest read-request). <see cref="SetAutonomousMode"/>, <see cref="SetColors(IReadOnlyDictionary{int, LampColor})"/>
/// and <see cref="SetAll"/> change the lighting.
/// </summary>
/// <remarks>
/// Colours only show while the device is out of autonomous mode (<c>SetAutonomousMode(false)</c>); in autonomous mode the
/// firmware runs its own effects (QLink LIGHTINGS). If this instance took control, <see cref="Dispose"/> hands it back
/// (autonomous mode on). If Windows Dynamic Lighting is enabled for the device, Windows drives it too — see
/// <see cref="DynamicLighting"/>.
/// </remarks>
public sealed class LampArrayDevice : IDisposable
{
    const int MaxPacingMicroseconds = 1_000_000;

    readonly ILampArrayTransport _transport;
    readonly Lock _gate = new();
    IReadOnlyList<LampInfo>? _lamps;
    LampMap? _map;
    long _lastUpdate;
    bool _hostControlled;
    bool _disposed;

    /// <param name="descriptor">The collection's report descriptor; the layout falls back to the Microsoft reference only if it cannot be parsed.</param>
    /// <param name="bindingKind">How to read InputBinding (the Dark Mount reports QLink key ids).</param>
    public LampArrayDevice(ILampArrayTransport transport, ReadOnlySpan<byte> descriptor,
        LampBindingKind bindingKind = LampBindingKind.Auto, string? devicePath = null)
    {
        _transport = transport;
        RawDescriptor = descriptor.ToArray();
        Layout = LampArrayLayout.ParseOrReference(descriptor, out var fallbackReason);
        LayoutWarning = fallbackReason;
        BindingKind = bindingKind;
        DevicePath = devicePath;
        BufferLength = Math.Max(Layout.MaxReportLength, transport.MaxFeatureReportLength);
        Attributes = ReadAttributes();
    }

    /// <summary>Opens the keyboard's LampArray collection (VID 0x373F, PID 0x0001, usage page 0x59), or null if absent.</summary>
    public static LampArrayDevice? Open()
    {
        var transport = HidSharpLampArrayTransport.TryOpen();
        if (transport is null) return null;
        try
        {
            return new LampArrayDevice(transport, transport.RawDescriptor, LampBindingKind.DarkmountKeyId, transport.DevicePath);
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens another maker's LampArray collection (found by <see cref="HidSharpLampArrayTransport.FindOthers"/>): key
    /// bindings are read as standard HID keyboard usages. Null when it can't be opened.
    /// </summary>
    public static LampArrayDevice? Open(HidSharp.HidDevice device, byte[] descriptor)
    {
        var transport = HidSharpLampArrayTransport.TryOpen(device, descriptor);
        if (transport is null) return null;
        try
        {
            return new LampArrayDevice(transport, descriptor, LampBindingKind.Auto, transport.DevicePath);
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    public LampArrayLayout Layout { get; }

    /// <summary>Set when the descriptor could not be parsed and the Microsoft reference layout is used instead.</summary>
    public string? LayoutWarning { get; }

    public byte[] RawDescriptor { get; }
    public string? DevicePath { get; }
    public LampBindingKind BindingKind { get; }

    /// <summary>Length of every feature-report buffer exchanged with the device (Windows pads all to the maximum).</summary>
    public int BufferLength { get; }

    public LampArrayAttributes Attributes { get; }

    /// <summary>All lamps, read once via the LampAttributesRequest → LampAttributesResponse loop.</summary>
    public IReadOnlyList<LampInfo> Lamps => _lamps ?? ReadLamps();

    /// <summary>Key ↔ lamp mapping built from <see cref="Lamps"/>.</summary>
    public LampMap Map => _map ??= LampMap.Build(Lamps, BindingKind);

    /// <summary>Wait so that complete updates are at least MinUpdateInterval apart (default on).</summary>
    public bool PaceUpdates { get; set; } = true;

    /// <summary>Re-reads every lamp's attributes (read-only).</summary>
    public IReadOnlyList<LampInfo> ReadLamps()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            int count = Attributes.LampCount;
            var lamps = new List<LampInfo>(count);
            if (count > 0) _transport.SetFeature(LampArrayReports.EncodeAttributesRequest(Layout, 0, BufferLength));
            for (int id = 0; id < count; id++)
            {
                // The device auto-increments the lamp id after each response; if it did not, ask for this lamp explicitly.
                var lamp = ReadLampResponse();
                if (lamp.Id != id)
                {
                    _transport.SetFeature(LampArrayReports.EncodeAttributesRequest(Layout, id, BufferLength));
                    lamp = ReadLampResponse();
                    if (lamp.Id != id) throw new InvalidDataException($"Requested lamp {id}, the device answered lamp {lamp.Id}.");
                }
                lamps.Add(lamp);
            }
            _map = null;
            return _lamps = lamps;
        }
    }

    /// <summary>true: the keyboard runs its own effects; false: the host (this app) controls every lamp.</summary>
    public void SetAutonomousMode(bool autonomous)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _transport.SetFeature(LampArrayReports.EncodeControl(Layout, autonomous, BufferLength));
            _hostControlled = !autonomous;
        }
    }

    /// <summary>Sets the given lamps (others keep their colour) with LampMultiUpdate reports; the last one completes the update.</summary>
    public void SetColors(IReadOnlyDictionary<int, LampColor> colors)
    {
        foreach (int id in colors.Keys) ValidateLampId(id);
        Send(LampArrayReports.EncodeMultiUpdates(Layout, colors, BufferLength));
    }

    public void SetColors(IReadOnlyDictionary<int, (byte R, byte G, byte B, byte I)> colors) =>
        SetColors(colors.ToDictionary(kv => kv.Key, kv => (LampColor)kv.Value));

    /// <summary>
    /// Sends one animation frame: optional same-colour runs as range updates, then per-lamp colours as multi-updates;
    /// the very last report carries "update complete". Callers should pass only lamps that changed.
    /// </summary>
    public void SetFrame(IReadOnlyDictionary<int, LampColor> perLamp, IReadOnlyList<(int First, int Last, LampColor Color)> ranges)
    {
        foreach (int id in perLamp.Keys) ValidateLampId(id);
        var reports = new List<byte[]>();
        for (int i = 0; i < ranges.Count; i++)
        {
            var (first, last, color) = ranges[i];
            ValidateLampId(first);
            ValidateLampId(last);
            bool complete = perLamp.Count == 0 && i == ranges.Count - 1;
            reports.Add(LampArrayReports.EncodeRangeUpdate(Layout, first, last, color, complete, BufferLength));
        }
        if (perLamp.Count > 0) reports.AddRange(LampArrayReports.EncodeMultiUpdates(Layout, perLamp, BufferLength));
        Send(reports);
    }

    /// <summary>Sets every lamp to one colour with a single LampRangeUpdate report.</summary>
    public void SetAll(LampColor color)
    {
        if (Attributes.LampCount == 0) return;
        Send([LampArrayReports.EncodeRangeUpdate(Layout, 0, Attributes.LampCount - 1, color, updateComplete: true, BufferLength)]);
    }

    LampArrayAttributes ReadAttributes()
    {
        var buffer = Layout.Attributes.NewBuffer(BufferLength);
        lock (_gate) _transport.GetFeature(buffer);
        return LampArrayReports.DecodeAttributes(Layout, buffer);
    }

    LampInfo ReadLampResponse()
    {
        var buffer = Layout.AttributesResponse.NewBuffer(BufferLength);
        _transport.GetFeature(buffer);
        return LampArrayReports.DecodeLampAttributes(Layout, buffer);
    }

    void Send(IReadOnlyList<byte[]> reports)
    {
        if (reports.Count == 0) return;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (PaceUpdates && _lastUpdate != 0)
            {
                var interval = TimeSpan.FromMicroseconds(Math.Clamp(Attributes.MinUpdateIntervalMicroseconds, 0, MaxPacingMicroseconds));
                var wait = interval - Stopwatch.GetElapsedTime(_lastUpdate);
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            }
            foreach (var report in reports) _transport.SetFeature(report);
            _lastUpdate = Stopwatch.GetTimestamp();
        }
    }

    void ValidateLampId(int id)
    {
        if (id < 0 || id >= Attributes.LampCount)
            throw new ArgumentOutOfRangeException(nameof(id), id, $"Lamp ids are 0..{Attributes.LampCount - 1}.");
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (_hostControlled) _transport.SetFeature(LampArrayReports.EncodeControl(Layout, true, BufferLength));
            }
            catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException)
            {
                // Device already gone (unplugged/asleep): nothing left to hand back.
            }
            _disposed = true;
            _transport.Dispose();
        }
    }
}
