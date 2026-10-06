using HidSharp;

namespace Darkmount.Keyboard.Lamps;

/// <summary>Feature-report access to a HID LampArray collection. Buffers start with the report id.</summary>
public interface ILampArrayTransport : IDisposable
{
    /// <summary>Feature report length the OS expects (including the report-id byte).</summary>
    int MaxFeatureReportLength { get; }

    /// <summary>Fills <paramref name="buffer"/> (byte 0 = report id to read).</summary>
    void GetFeature(byte[] buffer);

    void SetFeature(byte[] buffer);
}

/// <summary>
/// HidSharp transport for the Dark Mount's LampArray interface (VID 0x373F, PID 0x0001, usage page 0x59 — MI_03).
/// The vendor QLink interface (MI_02, usage page 0xFF00) is never selected because it has no LampArray collection.
/// </summary>
public sealed class HidSharpLampArrayTransport : ILampArrayTransport
{
    public const int VendorId = 0x373F;

    /// <summary>0 = any supported be quiet! keyboard (Dark Mount, Light Mount, Light Mount TKL).</summary>
    public const int ProductId = 0;

    readonly HidStream _stream;

    HidSharpLampArrayTransport(HidDevice device, HidStream stream, byte[] descriptor)
    {
        Device = device;
        _stream = stream;
        RawDescriptor = descriptor;
        MaxFeatureReportLength = device.GetMaxFeatureReportLength();
    }

    public HidDevice Device { get; }
    public string DevicePath => Device.DevicePath;

    /// <summary>Report descriptor as HidSharp returns it (on Windows: reconstructed from the preparsed data).</summary>
    public byte[] RawDescriptor { get; }

    public int MaxFeatureReportLength { get; }

    /// <summary>Finds the LampArray collection of the keyboard without opening it (read-only enumeration).</summary>
    public static (HidDevice Device, byte[] Descriptor)? Find(int vendorId = VendorId, int productId = ProductId)
    {
        var devices = productId != 0
            ? DeviceList.Local.GetHidDevices(vendorId, productId)
            : DeviceList.Local.GetHidDevices(vendorId).Where(d => Darkmount.QLink.KeyboardModel.ForProductId(d.ProductID) is not null);
        foreach (var device in devices)
        {
            byte[] descriptor;
            try
            {
                if (device.GetMaxFeatureReportLength() <= 1) continue;
                descriptor = device.GetRawReportDescriptor();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                continue;
            }
            if (IsLampArray(descriptor)) return (device, descriptor);
        }
        return null;
    }

    /// <summary>Opens the keyboard's LampArray collection, or returns null when it is not present / cannot be opened.</summary>
    public static HidSharpLampArrayTransport? TryOpen(int vendorId = VendorId, int productId = ProductId)
    {
        if (Find(vendorId, productId) is not { } found) return null;
        return TryOpen(found.Device, found.Descriptor);
    }

    /// <summary>Opens a LampArray collection found by <see cref="Find"/> or <see cref="FindOthers"/>; null when it can't be opened.</summary>
    public static HidSharpLampArrayTransport? TryOpen(HidDevice device, byte[] descriptor)
    {
        if (!device.TryOpen(out HidStream stream)) return null;
        stream.ReadTimeout = 1000;
        stream.WriteTimeout = 1000;
        return new HidSharpLampArrayTransport(device, stream, descriptor);
    }

    /// <summary>
    /// Every other LampArray collection on the PC (the Windows Dynamic Lighting standard: keyboards and laptops from
    /// Razer, Logitech, ASUS, Corsair…), be quiet! keyboards excluded. Read-only enumeration: nothing is opened.
    /// </summary>
    public static IReadOnlyList<(HidDevice Device, byte[] Descriptor)> FindOthers()
    {
        var found = new List<(HidDevice, byte[])>();
        foreach (var device in DeviceList.Local.GetHidDevices())
        {
            if (device.VendorID == VendorId) continue;
            byte[] descriptor;
            try
            {
                if (device.GetMaxFeatureReportLength() <= 1) continue;
                descriptor = device.GetRawReportDescriptor();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                continue;
            }
            if (IsLampArray(descriptor)) found.Add((device, descriptor));
        }
        return found;
    }

    /// <summary>
    /// True when the descriptor has a LampArray application collection. If our parser rejects the descriptor, the
    /// canonical opening (Usage Page 0x59, Usage 0x01, Collection Application) still identifies it, so the device opens
    /// and <see cref="LampArrayLayout.ParseOrReference"/> falls back to the reference layout.
    /// </summary>
    internal static bool IsLampArray(byte[] descriptor)
    {
        try
        {
            const uint application = ((uint)LampArrayUsages.Page << 16) | LampArrayUsages.LampArray;
            return HidReportDescriptor.Parse(descriptor).Fields.Any(f => f.ApplicationUsage == application);
        }
        catch (FormatException)
        {
            ReadOnlySpan<byte> opening = [0x05, 0x59, 0x09, 0x01, 0xA1, 0x01];
            return descriptor.AsSpan().IndexOf(opening) >= 0;
        }
    }

    public void GetFeature(byte[] buffer) => _stream.GetFeature(buffer);

    public void SetFeature(byte[] buffer) => _stream.SetFeature(buffer);

    public void Dispose() => _stream.Dispose();
}
