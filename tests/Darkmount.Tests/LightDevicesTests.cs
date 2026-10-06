using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Darkmount.App.LightDevices;
using Darkmount.App.Setup;
using Darkmount.Keyboard.Lamps;
using Darkmount.Keyboard.Lamps.OpenRgb;

namespace Darkmount.Tests;

/// <summary>Lighting on keyboards other than be quiet!'s: the Windows standard layout, OpenRGB output and setup.</summary>
public class LightDevicesTests
{
    static LampInfo Lamp(int id, int x, int y, int binding = 0) =>
        new(id, x, y, 0, 1000, LampPurposes.Control, 255, 255, 255, null, true, binding);

    // ---------------------------------------------------------------- Windows standard (LampArray) layouts

    [Fact]
    public void Standard_keyboards_use_their_own_positions_and_hid_bindings()
    {
        var lamps = new[] { Lamp(0, 1000, 500, 0x04), Lamp(1, 21000, 500, 0x16), Lamp(2, 11000, 4500, 0x2C), Lamp(3, 0, 0) }; // A, S, Space, logo
        var layout = RgbEffects.LayoutFromPositions(lamps, LampMap.Build(lamps), keyboard: true);

        Assert.All(layout, p => Assert.InRange(p.X, 0, 1));
        Assert.All(layout, p => Assert.InRange(p.Y, 0, 1));
        Assert.Equal((1.0, 0.5 / 4.5), (layout[1].X, layout[1].Y), new PairTolerance());
        Assert.Equal(DarkmountKeys.ByUsage[0x04].Id, layout[0].KeyId);
        Assert.True(layout[2].IsKey);
        Assert.False(layout[3].IsKey); // other lamps are bound to keys, this one isn't
    }

    [Fact]
    public void Keyboards_without_key_bindings_count_every_lamp_as_a_key()
    {
        var zones = new[] { Lamp(0, 0, 0), Lamp(1, 100, 0), Lamp(2, 200, 0), Lamp(3, 300, 0) }; // a 4-zone laptop
        var layout = RgbEffects.LayoutFromPositions(zones, LampMap.Build(zones, LampBindingKind.HidKeyboardUsage), keyboard: true);

        Assert.All(layout, p => Assert.True(p.IsKey));
        Assert.Equal([0, 1 / 3.0, 2 / 3.0, 1], layout.Select(p => Math.Round(p.X, 6)).Select(x => x).ToArray(), new Tolerance());
        Assert.All(layout, p => Assert.Equal(0.5, p.Y));
        var single = RgbEffects.LayoutFromPositions([Lamp(0, 7, 7)], LampMap.Build([Lamp(0, 7, 7)], LampBindingKind.HidKeyboardUsage), keyboard: true);
        Assert.Equal((0.5, 0.5), (single[0].X, single[0].Y));
    }

    // ---------------------------------------------------------------- which PCs get the OpenRGB offer

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "ASUS TUF Gaming F15 FX506HM_FX506HM", "ASUS TUF Gaming F15 FX506HM_FX506HM")]
    [InlineData("ASUSTeK COMPUTER INC.", "ROG Strix G513QM", "ASUS ROG Strix G513QM")]
    [InlineData("LENOVO", "Legion 5 15ACH6H", "LENOVO Legion 5 15ACH6H")]
    [InlineData("Micro-Star International Co., Ltd.", "Katana GF66 11UE", "Micro-Star International Co., Ltd. Katana GF66 11UE")]
    [InlineData("Micro-Star International Co., Ltd.", "GE76 Raider 11UH", "Micro-Star International Co., Ltd. GE76 Raider 11UH")]
    [InlineData("Micro-Star International Co., Ltd.", "Prestige 14 A11SC", null)]
    [InlineData("ASUS", "System Product Name", null)]
    [InlineData("HP", "HP Pavilion Laptop 15-eg0xxx", null)]
    [InlineData("HP", "OMEN by HP Laptop 16-b0xxx", "HP OMEN by HP Laptop 16-b0xxx")]
    [InlineData("Dell Inc.", "XPS 15 9520", null)]
    [InlineData(null, "TUF Gaming", null)]
    public void Gaming_laptops_are_recognised_by_maker_and_model(string? maker, string? model, string? expected) =>
        Assert.Equal(expected, OpenRgbSetup.GamingLaptop(maker, model));

    [Fact]
    public void Openrgb_starts_with_windows_from_an_elevated_logon_task()
    {
        var xml = OpenRgbSetup.TaskXml(@"C:\Program Files\OpenRGB\OpenRGB.exe", "S-1-5-21-1-2-3-1001");
        var task = System.Xml.Linq.XDocument.Parse(xml).Root!;
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("HighestAvailable", task.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("S-1-5-21-1-2-3-1001", task.Descendants(ns + "LogonTrigger").Single().Descendants(ns + "UserId").Single().Value);
        Assert.Equal(@"C:\Program Files\OpenRGB\OpenRGB.exe", task.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--server --startminimized", task.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("PT0S", task.Descendants(ns + "ExecutionTimeLimit").Single().Value);
    }

    // ---------------------------------------------------------------- start-up questions

    /// <summary>Runs Windows Forms' own page check (the one TaskDialog.ShowDialog runs before showing anything).</summary>
    static void Validate(TaskDialogPage page) =>
        typeof(TaskDialogPage).GetMethod("Validate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(page, null);

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Io_center_question_is_a_valid_dialog(bool running, bool autostart)
    {
        var (page, recommended, _) = Darkmount.App.TrayApp.IoCenterPage(running, autostart);
        Validate(page); // throws if command links and ordinary buttons were mixed (the 1.5.0 bug)
        Assert.Contains(recommended, page.Buttons);
        Assert.Equal(running && autostart ? 3 : 2, page.Buttons.Count);
    }

    [Fact]
    public void Other_startup_questions_are_valid_dialogs()
    {
        Validate(Darkmount.App.TrayApp.OpenRgbPage("ASUS TUF Gaming F15", installed: false).Page);
        Validate(Darkmount.App.TrayApp.OpenRgbPage("Razer keyboard", installed: true).Page);
        Validate(Darkmount.App.TrayApp.SensorUpdatePage(new Version(1, 6, 0), new Version(1, 3, 0), new TaskDialogButton("Update now")));
    }

    // ---------------------------------------------------------------- OpenRGB output against a fake server

    static OpenRgbController Laptop(int index) => new(index, OpenRgbDeviceType.Laptop, "ASUS TUF Gaming F15", "ASUS", "TUF", "", "WMI",
        [new OpenRgbMode("Static", 1u << 6, 2, [1, 2, 3]), new OpenRgbMode("Direct", 1u << 5, 1, [9, 9])], ActiveMode: 0,
        [new OpenRgbZone("Keyboard", 0, 0, 4, 0, 0, [])], ["Zone 1", "Zone 2", "Zone 3", "Zone 4"]);

    static (uint Dev, uint Id, byte[] Payload) Read(NetworkStream s)
    {
        var header = new byte[16];
        s.ReadExactly(header);
        var payload = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12))];
        s.ReadExactly(payload);
        return (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)), payload);
    }

    /// <summary>A fake OpenRGB: handshake, an empty device list, then records the next <paramref name="count"/> packets.</summary>
    static (TcpListener Listener, Task Server, List<(uint Dev, uint Id, byte[] Payload)> Received) FakeOpenRgb(int count)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var received = new List<(uint Dev, uint Id, byte[] Payload)>();
        var server = Task.Run(() =>
        {
            using var c = listener.AcceptTcpClient();
            using var s = c.GetStream();
            s.ReadTimeout = 10_000;
            Read(s);                                                           // client name
            Read(s);                                                           // version request
            s.Write([.. "ORGB"u8, 0, 0, 0, 0, 40, 0, 0, 0, 4, 0, 0, 0, 5, 0, 0, 0]);
            Assert.Equal(0u, Read(s).Id);                                      // controller count
            s.Write([.. "ORGB"u8, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0]);
            for (int i = 0; i < count; i++)
            {
                var packet = Read(s); // outside the lock: the test reads the list while this waits
                lock (received) received.Add(packet);
            }
        });
        return (listener, server, received);
    }

    static OpenRgbLink Link(TcpListener listener)
    {
        var link = new OpenRgbLink(() => OpenRgbClient.TryConnect("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, timeout: TimeSpan.FromSeconds(5)));
        for (int i = 0; i < 500 && !link.Connected; i++) Thread.Sleep(10);
        Assert.True(link.Connected);
        return link;
    }

    [Fact]
    public async Task Openrgb_output_switches_to_direct_sends_changes_only_and_restores_the_mode()
    {
        var (listener, server, received) = FakeOpenRgb(4);
        try
        {
            using (var link = Link(listener))
            {
                var output = new OpenRgbOutput(link, Laptop(index: 3), link.Generation);
                Assert.Equal("openrgb:ASUS TUF Gaming F15|WMI", output.Id);
                Assert.All(output.Layout, p => Assert.True(p.IsKey)); // keyboard zone LEDs light like keys

                var red = new Dictionary<int, LampColor> { [0] = new(255, 0, 0), [1] = new(255, 0, 0), [2] = new(255, 0, 0), [3] = new(255, 0, 0) };
                output.Send(red);
                Assert.True(link.WaitIdle(TimeSpan.FromSeconds(5)));
                output.Send(red); // unchanged: nothing posted
                output.Send(new Dictionary<int, LampColor> { [0] = new(0, 0, 255) }); // missing LEDs → black
                Assert.True(link.WaitIdle(TimeSpan.FromSeconds(5)));
                output.HandBack();
            } // the link sends the last hand-back before it closes
            await server.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { listener.Stop(); }

        Assert.Equal([1100u, 1050u, 1050u, 1101u], received.Select(r => r.Id));
        Assert.All(received, r => Assert.Equal(3u, r.Dev));
        Assert.Equal(0x000000FFu, BinaryPrimitives.ReadUInt32LittleEndian(received[1].Payload.AsSpan(6)));
        Assert.Equal(0x00FF0000u, BinaryPrimitives.ReadUInt32LittleEndian(received[2].Payload.AsSpan(6)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(received[2].Payload.AsSpan(10)));
        // Restores mode 0 ("Static") exactly as OpenRGB described it.
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(received[3].Payload.AsSpan(4)));
        Assert.Equal(new byte[] { 1, 2, 3 }, received[3].Payload[8..]);
    }

    [Fact]
    public void Openrgb_frames_for_an_outdated_device_list_are_never_sent()
    {
        var (listener, _, received) = FakeOpenRgb(1);
        try
        {
            using var link = Link(listener);
            var stale = new OpenRgbOutput(link, Laptop(index: 1), link.Generation - 1); // listed before the list changed
            stale.Send(new Dictionary<int, LampColor> { [0] = new(1, 2, 3) });
            stale.HandBack();
            Assert.True(link.WaitIdle(TimeSpan.FromSeconds(5)));
            Thread.Sleep(200);
            lock (received) Assert.Empty(received); // that index may be another device now
        }
        finally { listener.Stop(); }
    }

    sealed class Tolerance : IEqualityComparer<double>
    {
        public bool Equals(double a, double b) => Math.Abs(a - b) < 1e-6;
        public int GetHashCode(double d) => 0;
    }

    sealed class PairTolerance : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) a, (double, double) b) => Math.Abs(a.Item1 - b.Item1) < 1e-6 && Math.Abs(a.Item2 - b.Item2) < 1e-6;
        public int GetHashCode((double, double) d) => 0;
    }
}
