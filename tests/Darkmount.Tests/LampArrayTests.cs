using System.Runtime.CompilerServices;
using Darkmount.Keyboard.Lamps;
using static Darkmount.Keyboard.Lamps.LampArrayUsages;

namespace Darkmount.Tests;

public class LampArrayTests
{
    const string FixtureName = "darkmount-lamparray-descriptor.bin";

    // Captured read-only from the real keyboard (MI_03) with HidSharp: GetFeature(1) and GetFeature(3) after SetFeature(2, lamp 0).
    const string RealAttributes = "01C900C0B60600E022020050C3000001000000358200000000000000000000000000000000000000000000";
    const string RealLamp0 = "030000A08601000000000000000000E803000004000000FFFFFF016A000000000000000000000000000000";
    const string RealLamp26Esc = "031A00E2C10100606D000000000000E803000004000000FFFFFF0157000000000000000000000000000000";
    const string RealLamp46Grave = "032E00E2C10100C0DA000000000000E803000004000000FFFFFF0101000000000000000000000000000000";
    const string RealLamp199 = "03C70056DA0100E022020000000000E803000004000000FFFFFF01BB000000000000000000000000000000";

    static byte[] RealDescriptor() => File.ReadAllBytes(FixturePath());

    static string FixturePath([CallerFilePath] string source = "")
    {
        var beside = Path.Combine(Path.GetDirectoryName(source) ?? "", "Fixtures", FixtureName);
        if (File.Exists(beside)) return beside;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Darkmount.Tests", "Fixtures", FixtureName);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("LampArray descriptor fixture not found.", FixtureName);
    }

    static LampArrayLayout RealLayout() => LampArrayLayout.Parse(RealDescriptor());

    static void AssertField(LampReportLayout report, ushort usage, int byteOffset, int bits, int index = 0)
    {
        var f = report.Find(usage, index);
        Assert.True(f is not null, $"report {report.ReportId}: usage 0x{usage:X2} #{index} missing");
        Assert.Equal(byteOffset * 8, f.Value.BitOffset);
        Assert.Equal(bits, f.Value.BitSize);
    }

    static string Hex(byte[] b) => Convert.ToHexString(b);
    static string Zeros(int bytes) => new('0', bytes * 2);

    // ---- Descriptor parser on the real fixture ----

    [Fact]
    public void RealDescriptor_GenericParserSeesSixPaddedFeatureReports()
    {
        var bytes = RealDescriptor();
        Assert.Equal(406, bytes.Length);
        var d = HidReportDescriptor.Parse(bytes);

        Assert.True(d.UsesReportIds);
        var reports = d.Reports.OrderBy(r => r.ReportId).ToList();
        Assert.Equal([1, 2, 3, 4, 5, 6], reports.Select(r => (int)r.ReportId));
        Assert.All(reports, r => Assert.Equal(HidReportKind.Feature, r.Kind));
        // Windows reports one feature length (43 incl. id); HidSharp's reconstruction pads every report to it.
        Assert.All(reports, r => Assert.Equal(43, d.GetReportLength(HidReportKind.Feature, r.ReportId)));
        Assert.All(d.Fields, f => Assert.Equal(0x00590001u, f.ApplicationUsage));

        // "26 FF FF" / "25 FF" with a non-negative minimum are unsigned maxima.
        var lampId = d.Fields.First(f => f.ReportId == 2 && f.UsageId == LampId);
        Assert.Equal(65535, lampId.LogicalMaximum);
        Assert.Equal(255, d.Fields.First(f => f.ReportId == 4 && f.UsageId == RedUpdateChannel).LogicalMaximum);
        Assert.Equal(8, d.Fields.First(f => f.ReportId == 4 && f.UsageId == LampCount).LogicalMaximum);
        Assert.True(d.Fields.First(f => f.ReportId == 1 && f.UsageId == LampCount).IsConstant);
    }

    [Fact]
    public void RealDescriptor_LampArrayLayout()
    {
        var layout = RealLayout();

        Assert.False(layout.IsMicrosoftReference);
        Assert.Equal([1, 2, 3, 4, 5, 6], layout.Reports.Select(r => (int)r.ReportId));
        Assert.All(layout.Reports, r => Assert.Equal(43, r.Length));
        Assert.Equal(8, layout.MaxLampsPerMultiUpdate);
        Assert.False(layout.HasIntensityChannel);

        var a = layout.Attributes;
        AssertField(a, LampCount, 1, 16);
        AssertField(a, BoundingBoxWidthInMicrometers, 3, 32);
        AssertField(a, BoundingBoxHeightInMicrometers, 7, 32);
        AssertField(a, BoundingBoxDepthInMicrometers, 11, 32);
        AssertField(a, LampArrayKindUsage, 15, 32);
        AssertField(a, MinUpdateIntervalInMicroseconds, 19, 32);

        AssertField(layout.AttributesRequest, LampId, 1, 16);

        var r = layout.AttributesResponse;
        AssertField(r, LampId, 1, 16);
        AssertField(r, PositionXInMicrometers, 3, 32);
        AssertField(r, PositionYInMicrometers, 7, 32);
        AssertField(r, PositionZInMicrometers, 11, 32);
        AssertField(r, UpdateLatencyInMicroseconds, 15, 32);
        AssertField(r, LampPurposesUsage, 19, 32);
        AssertField(r, RedLevelCount, 23, 8);
        AssertField(r, GreenLevelCount, 24, 8);
        AssertField(r, BlueLevelCount, 25, 8);
        AssertField(r, IsProgrammable, 26, 8);
        AssertField(r, InputBinding, 27, 8);
        Assert.Null(r.Find(IntensityLevelCount));

        var m = layout.MultiUpdate;
        AssertField(m, LampCount, 1, 8);
        AssertField(m, LampUpdateFlags, 2, 8);
        for (int k = 0; k < 8; k++)
        {
            AssertField(m, LampId, 3 + 2 * k, 16, k);
            AssertField(m, RedUpdateChannel, 19 + 3 * k, 8, k);
            AssertField(m, GreenUpdateChannel, 20 + 3 * k, 8, k);
            AssertField(m, BlueUpdateChannel, 21 + 3 * k, 8, k);
        }
        Assert.Equal(0, m.CountOf(IntensityUpdateChannel));

        var g = layout.RangeUpdate;
        AssertField(g, LampUpdateFlags, 1, 8);
        AssertField(g, LampIdStart, 2, 16);
        AssertField(g, LampIdEnd, 4, 16);
        AssertField(g, RedUpdateChannel, 6, 8);
        AssertField(g, GreenUpdateChannel, 7, 8);
        AssertField(g, BlueUpdateChannel, 8, 8);

        AssertField(layout.Control, AutonomousMode, 1, 8);
    }

    [Fact]
    public void MicrosoftReference_Layout()
    {
        var layout = LampArrayLayout.MicrosoftReference;

        Assert.True(layout.IsMicrosoftReference);
        Assert.Equal([1, 2, 3, 4, 5, 6], layout.Reports.Select(r => (int)r.ReportId));
        Assert.Equal([23, 3, 29, 51, 10, 2], layout.Reports.Select(r => r.Length));
        Assert.Equal(8, layout.MaxLampsPerMultiUpdate);
        Assert.True(layout.HasIntensityChannel);
        AssertField(layout.AttributesResponse, IntensityLevelCount, 26, 8);
        AssertField(layout.AttributesResponse, InputBinding, 28, 8);
        AssertField(layout.MultiUpdate, RedUpdateChannel, 19 + 4 * 7, 8, 7);
        AssertField(layout.MultiUpdate, IntensityUpdateChannel, 22 + 4 * 7, 8, 7);
        AssertField(layout.RangeUpdate, IntensityUpdateChannel, 9, 8);
    }

    [Fact]
    public void ParseOrReference_FallsBackOnlyWhenParsingFails()
    {
        var parsed = LampArrayLayout.ParseOrReference(RealDescriptor(), out var none);
        Assert.Null(none);
        Assert.False(parsed.IsMicrosoftReference);

        var truncated = RealDescriptor()[..100];
        var fallback = LampArrayLayout.ParseOrReference(truncated, out var reason);
        Assert.True(fallback.IsMicrosoftReference);
        Assert.NotNull(reason);

        // A plain keyboard descriptor has no LampArray at all.
        byte[] keyboard = [0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25, 0x01,
                           0x75, 0x01, 0x95, 0x08, 0x81, 0x02, 0xC0];
        Assert.False(LampArrayLayout.TryParse(keyboard, out _, out var error));
        Assert.Contains("No LampArray", error);
    }

    [Fact]
    public void Parser_RejectsMalformedDescriptors()
    {
        Assert.Throws<FormatException>(() => HidReportDescriptor.Parse([0x05, 0x59, 0x27, 0xFF])); // truncated 4-byte item
        Assert.Throws<FormatException>(() => HidReportDescriptor.Parse([0x09, 0x01, 0xA1, 0x01])); // unclosed collection
        Assert.Throws<FormatException>(() => HidReportDescriptor.Parse([0xC0]));                   // end without collection
        Assert.Throws<FormatException>(() => HidReportDescriptor.Parse([0x85, 0x00]));             // report id 0
    }

    /// <summary>No logical collections, 8-bit lamp ids, Usage Minimum/Maximum colour ranges, 4 lamps per update.</summary>
    static readonly byte[] CompactDescriptor =
    [
        0x05, 0x59, 0x09, 0x01, 0xA1, 0x01, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08,
        0x85, 0x11, 0x09, 0x03, 0x09, 0x07, 0x95, 0x02, 0xB1, 0x03,                     // attributes: count, kind
        0x85, 0x12, 0x09, 0x21, 0x95, 0x01, 0xB1, 0x02,                                 // request
        0x85, 0x13, 0x09, 0x21, 0x09, 0x23, 0x09, 0x2D, 0x95, 0x03, 0xB1, 0x02,         // response: id, x, binding
        0x85, 0x14, 0x09, 0x03, 0x09, 0x55, 0x95, 0x02, 0xB1, 0x02, 0x09, 0x21, 0x95, 0x04, 0xB1, 0x02,
        0x19, 0x51, 0x29, 0x53, 0x95, 0x03, 0xB1, 0x02, 0x19, 0x51, 0x29, 0x53, 0xB1, 0x02,
        0x19, 0x51, 0x29, 0x53, 0xB1, 0x02, 0x19, 0x51, 0x29, 0x53, 0xB1, 0x02,         // multi: 4 × RGB
        0x85, 0x15, 0x09, 0x55, 0x95, 0x01, 0xB1, 0x02, 0x09, 0x61, 0x09, 0x62, 0x95, 0x02, 0xB1, 0x02,
        0x19, 0x51, 0x29, 0x53, 0x95, 0x03, 0xB1, 0x02,                                 // range
        0x85, 0x16, 0x09, 0x71, 0x95, 0x01, 0xB1, 0x02,                                 // control
        0xC0,
    ];

    [Fact]
    public void Parser_ClassifiesCollectionlessReportsAnd8BitIds()
    {
        var layout = LampArrayLayout.Parse(CompactDescriptor);

        Assert.Equal([0x11, 0x12, 0x13, 0x14, 0x15, 0x16], layout.Reports.Select(r => (int)r.ReportId));
        Assert.Equal(4, layout.MaxLampsPerMultiUpdate);
        AssertField(layout.MultiUpdate, LampId, 3, 8);
        AssertField(layout.MultiUpdate, LampId, 6, 8, 3);
        AssertField(layout.MultiUpdate, RedUpdateChannel, 7, 8);
        AssertField(layout.MultiUpdate, BlueUpdateChannel, 18, 8, 3);
        Assert.Equal(19, layout.MultiUpdate.Length);

        var reports = LampArrayReports.EncodeMultiUpdates(layout, Enumerable.Range(0, 5).ToDictionary(i => i, _ => new LampColor(9, 8, 7)));
        Assert.Equal(["14040000010203" + "090807090807090807090807", "14010104000000" + "090807" + Zeros(9)], reports.Select(Hex));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LampArrayReports.EncodeMultiUpdates(layout, new Dictionary<int, LampColor> { [256] = new(1, 1, 1) }));
    }

    // ---- Attribute / response decoding (real captures) ----

    [Fact]
    public void DecodeAttributes_RealCapture()
    {
        var a = LampArrayReports.DecodeAttributes(RealLayout(), Convert.FromHexString(RealAttributes));
        Assert.Equal(new LampArrayAttributes(201, 440_000, 140_000, 50_000, LampArrayKind.Keyboard, 33_333), a);
    }

    [Fact]
    public void DecodeLampAttributes_RealCaptures()
    {
        var layout = RealLayout();
        LampInfo Decode(string hex) => LampArrayReports.DecodeLampAttributes(layout, Convert.FromHexString(hex));

        Assert.Equal(new LampInfo(26, 115_170, 28_000, 0, 1000, LampPurposes.Branding, 255, 255, 255, null, true, 0x57), Decode(RealLamp26Esc));
        Assert.Equal(new LampInfo(0, 100_000, 0, 0, 1000, LampPurposes.Branding, 255, 255, 255, null, true, 0x6A), Decode(RealLamp0));
        Assert.Equal(0x01, Decode(RealLamp46Grave).InputBinding);
        var last = Decode(RealLamp199);
        Assert.Equal((199, 121_430, 140_000, 0xBB), (last.Id, last.PositionX, last.PositionY, last.InputBinding));

        Assert.Throws<InvalidDataException>(() => Decode(RealAttributes));   // wrong report id
        Assert.Throws<InvalidDataException>(() => Decode(RealLamp0[..20])); // too short
    }

    // ---- Report encoding: golden bytes from the parsed real layout ----

    [Fact]
    public void EncodeAttributesRequest_And_Control_Golden()
    {
        var layout = RealLayout();
        Assert.Equal("020201" + Zeros(40), Hex(LampArrayReports.EncodeAttributesRequest(layout, 0x0102)));
        Assert.Equal("0601" + Zeros(41), Hex(LampArrayReports.EncodeControl(layout, autonomousMode: true)));
        Assert.Equal("0600" + Zeros(41), Hex(LampArrayReports.EncodeControl(layout, autonomousMode: false)));
        Assert.Equal(64, LampArrayReports.EncodeControl(layout, true, bufferLength: 64).Length);
    }

    [Fact]
    public void EncodeMultiUpdate_Golden()
    {
        var colors = new Dictionary<int, LampColor>
        {
            [5] = new(0x10, 0x20, 0x30),
            [1] = new(0x01, 0x02, 0x03),
            [300] = new(0xAA, 0xBB, 0xCC),
        };
        var reports = LampArrayReports.EncodeMultiUpdates(RealLayout(), colors);

        var only = Assert.Single(reports);
        Assert.Equal("04" + "03" + "01" + "0100" + "0500" + "2C01" + Zeros(10) + "010203" + "102030" + "AABBCC" + Zeros(15), Hex(only));
    }

    [Fact]
    public void EncodeRangeUpdate_Golden()
    {
        var layout = RealLayout();
        Assert.Equal("0501" + "0000" + "C800" + "FF8000" + Zeros(34),
            Hex(LampArrayReports.EncodeRangeUpdate(layout, 0, 200, new LampColor(0xFF, 0x80, 0x00))));
        Assert.Equal("0500" + "0300" + "0700" + "010203" + Zeros(34),
            Hex(LampArrayReports.EncodeRangeUpdate(layout, 3, 7, new LampColor(1, 2, 3), updateComplete: false)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LampArrayReports.EncodeRangeUpdate(layout, 5, 4, default));
    }

    [Fact]
    public void Intensity_ScalesRgbWithoutChannel_WrittenWithReferenceLayout()
    {
        var color = new LampColor(200, 100, 50, 128);
        var real = LampArrayReports.EncodeRangeUpdate(RealLayout(), 0, 0, color);
        Assert.Equal(new byte[] { 100, 50, 25 }, real[6..9]);

        var reference = LampArrayReports.EncodeRangeUpdate(LampArrayLayout.MicrosoftReference, 0, 0, color);
        Assert.Equal("050100000000C8643280", Hex(reference));

        var multi = LampArrayReports.EncodeMultiUpdates(LampArrayLayout.MicrosoftReference, new Dictionary<int, LampColor> { [2] = color });
        Assert.Equal("0401010200" + Zeros(14) + "C8643280" + Zeros(28), Hex(Assert.Single(multi)));
    }

    // ---- Batching boundaries ----

    [Theory]
    [InlineData(0, new int[0])]
    [InlineData(1, new[] { 1 })]
    [InlineData(8, new[] { 8 })]
    [InlineData(9, new[] { 8, 1 })]
    [InlineData(16, new[] { 8, 8 })]
    [InlineData(17, new[] { 8, 8, 1 })]
    [InlineData(201, new[] { 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 1 })]
    public void EncodeMultiUpdate_Batching(int lamps, int[] expectedCounts)
    {
        var colors = Enumerable.Range(0, lamps).Reverse().ToDictionary(i => i, i => new LampColor((byte)i, 0, 0));
        var reports = LampArrayReports.EncodeMultiUpdates(RealLayout(), colors);

        Assert.Equal(expectedCounts, reports.Select(r => (int)r[1]));
        Assert.All(reports, r => Assert.Equal(43, r.Length));
        Assert.All(reports, r => Assert.Equal(4, r[0]));
        // Only the last report completes the update.
        Assert.Equal(reports.Select((_, i) => i == reports.Count - 1 ? 1 : 0), reports.Select(r => (int)r[2]));
        // Every lamp exactly once, ascending, with its colour in the matching slot.
        var sent = reports.SelectMany(r => Enumerable.Range(0, r[1]).Select(k => (Id: r[3 + 2 * k] | r[4 + 2 * k] << 8, Red: r[19 + 3 * k]))).ToList();
        Assert.Equal(Enumerable.Range(0, lamps), sent.Select(s => s.Id));
        Assert.All(sent, s => Assert.Equal((byte)s.Id, s.Red));
    }

    [Fact]
    public void EncodeMultiUpdate_RejectsDuplicatesAndNegativeIds()
    {
        var layout = RealLayout();
        KeyValuePair<int, LampColor>[] duplicate = [new(3, default), new(3, default)];
        Assert.Throws<ArgumentException>(() => LampArrayReports.EncodeMultiUpdates(layout, duplicate));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LampArrayReports.EncodeMultiUpdates(layout, new Dictionary<int, LampColor> { [-1] = default }));
    }

    // ---- LampArrayDevice over a simulated keyboard (no hardware) ----

    /// <summary>Simulates the keyboard's MI_03 feature reports using the real on-wire format (independent of the parser).</summary>
    sealed class FakeLampTransport(int lampCount, bool autoIncrement = true) : ILampArrayTransport
    {
        int _next;
        public List<byte[]> Written { get; } = [];
        public List<byte> ReadIds { get; } = [];
        public bool Disposed { get; private set; }
        public int MaxFeatureReportLength => 43;

        public void GetFeature(byte[] buffer)
        {
            Assert.Equal(43, buffer.Length);
            ReadIds.Add(buffer[0]);
            Array.Clear(buffer, 1, buffer.Length - 1);
            switch (buffer[0])
            {
                case 1:
                    Convert.FromHexString(RealAttributes).CopyTo(buffer, 0);
                    buffer[1] = (byte)lampCount;
                    buffer[2] = (byte)(lampCount >> 8);
                    break;
                case 3:
                    buffer[1] = (byte)_next;
                    buffer[2] = (byte)(_next >> 8);
                    BitConverter.TryWriteBytes(buffer.AsSpan(3), 1000 * _next);  // x
                    BitConverter.TryWriteBytes(buffer.AsSpan(15), 1000);         // latency
                    buffer[19] = (byte)LampPurposes.Control;
                    buffer[23] = buffer[24] = buffer[25] = 255;
                    buffer[26] = 1;
                    buffer[27] = (byte)(_next + 1);                              // binding = key id
                    if (autoIncrement) _next = Math.Min(_next + 1, lampCount - 1);
                    break;
                default:
                    throw new InvalidOperationException($"unexpected GetFeature({buffer[0]})");
            }
        }

        public void SetFeature(byte[] buffer)
        {
            Assert.Equal(43, buffer.Length);
            Written.Add(buffer.ToArray());
            if (buffer[0] == 2) _next = buffer[1] | buffer[2] << 8;
        }

        public void Dispose() => Disposed = true;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Device_EnumeratesLampsReadOnly(bool autoIncrement)
    {
        var t = new FakeLampTransport(5, autoIncrement);
        using (var device = new LampArrayDevice(t, RealDescriptor(), LampBindingKind.DarkmountKeyId))
        {
            Assert.Equal(5, device.Attributes.LampCount);
            Assert.Equal(33_333, device.Attributes.MinUpdateIntervalMicroseconds);
            Assert.Equal([0, 1, 2, 3, 4], device.Lamps.Select(l => l.Id));
            Assert.Equal([0, 1000, 2000, 3000, 4000], device.Lamps.Select(l => l.PositionX));
            Assert.Equal("`", device.Map.Describe(0));
            Assert.Equal(1, device.Map.UsageToLamp[0x1E]); // "1" key (key id 2) on lamp 1
        }

        // Only the attribute read, the request report and response reads — nothing that changes the lighting.
        Assert.All(t.Written, w => Assert.Equal(2, w[0]));
        Assert.Equal(autoIncrement ? 1 : 5, t.Written.Count);
        Assert.All(t.ReadIds, id => Assert.Contains(id, new byte[] { 1, 3 }));
        Assert.True(t.Disposed);
    }

    [Fact]
    public void Device_WritesBatchedReportsAndRestoresAutonomousModeOnDispose()
    {
        var t = new FakeLampTransport(201);
        using (var device = new LampArrayDevice(t, RealDescriptor()) { PaceUpdates = false })
        {
            device.SetAutonomousMode(false);
            device.SetColors(Enumerable.Range(0, 10).ToDictionary(i => i, _ => new LampColor(1, 2, 3)));
            device.SetColors(new Dictionary<int, (byte, byte, byte, byte)> { [200] = (9, 9, 9, 255) });
            device.SetAll(new LampColor(0, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => device.SetColors(new Dictionary<int, LampColor> { [201] = default }));
        }

        Assert.Equal([6, 4, 4, 4, 5, 6], t.Written.Select(w => (int)w[0]));
        Assert.Equal(0, t.Written[0][1]);                                    // host control
        Assert.Equal((8, 0), (t.Written[1][1], t.Written[1][2]));            // 8 lamps, not complete
        Assert.Equal((2, 1), (t.Written[2][1], t.Written[2][2]));            // 2 lamps, complete
        Assert.Equal("0401" + "01" + "C800", Hex(t.Written[3])[..10]);
        Assert.Equal("0501" + "0000" + "C800" + "000000", Hex(t.Written[4])[..18]); // SetAll: lamps 0..200
        Assert.Equal(1, t.Written[5][1]);                                    // Dispose → autonomous again
    }

    [Fact]
    public void Output_takes_control_sends_only_changes_and_hands_back()
    {
        var t = new FakeLampTransport(4);
        var device = new LampArrayDevice(t, RealDescriptor()) { PaceUpdates = false };
        var layout = Enumerable.Range(0, 4).Select(i => new LampPoint(i, i / 3.0, 0.5, true, i + 1)).ToList();
        var output = new Darkmount.App.LightDevices.LampArrayOutput(device, "std:1234:5678", "Test", Darkmount.App.LightDevices.LightVia.WindowsStandard, layout);
        var frame = new Dictionary<int, LampColor> { [0] = new(9, 9, 9), [1] = new(9, 9, 9), [2] = new(9, 9, 9), [3] = new(1, 2, 3) };
        int before = t.Written.Count;

        output.Send(frame);
        Assert.Equal([6, 5, 4], t.Written.Skip(before).Select(w => (int)w[0])); // host control, one range (0..2), lamp 3
        Assert.Equal(0, t.Written[before][1]);

        output.Send(frame);                                                     // unchanged: nothing written
        Assert.Equal(before + 3, t.Written.Count);

        output.Send(new Dictionary<int, LampColor>(frame) { [3] = new(7, 7, 7) });
        Assert.Equal(4, t.Written[^1][0]);
        Assert.Equal(before + 4, t.Written.Count);

        output.Dispose();
        Assert.Equal((6, 1), (t.Written[^1][0], t.Written[^1][1]));              // autonomous mode again
        Assert.True(t.Disposed);
        Assert.Equal(new Dictionary<int, int> { [1] = 0, [2] = 1, [3] = 2, [4] = 3 }, output.LampOfKey);
    }

    [Fact]
    public void Device_PacesCompleteUpdatesByMinUpdateInterval()
    {
        var t = new FakeLampTransport(3);
        using var device = new LampArrayDevice(t, RealDescriptor());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        device.SetAll(new LampColor(1, 1, 1));
        device.SetAll(new LampColor(2, 2, 2));
        device.SetAll(new LampColor(3, 3, 3));
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(60), $"elapsed {sw.Elapsed.TotalMilliseconds} ms");
    }

    // ---- Lamp → key mapping ----

    static LampInfo Lamp(int id, int binding, int x = 0, int y = 0) =>
        new(id, x, y, 0, 1000, LampPurposes.Branding, 255, 255, 255, null, true, binding);

    [Fact]
    public void LampMap_DarkmountKeyIdBindings()
    {
        var layout = RealLayout();
        var lamps = new[] { RealLamp0, RealLamp26Esc, RealLamp46Grave }
            .Select(h => LampArrayReports.DecodeLampAttributes(layout, Convert.FromHexString(h)))
            .Append(Lamp(65, 0x0F))   // Tab
            .Append(Lamp(122, 0x37))  // Fn (no HID usage)
            .Append(Lamp(151, 0x69))  // ISO key
            .ToList();

        Assert.Equal(LampBindingKind.DarkmountKeyId, LampMap.DetectBindingKind(lamps));
        var map = LampMap.Build(lamps);

        Assert.Equal(26, map.UsageToLamp[0x29]);   // Esc
        Assert.Equal(46, map.UsageToLamp[0x35]);   // `
        Assert.Equal(65, map.UsageToLamp[0x2B]);   // Tab
        Assert.Equal(151, map.UsageToLamp[0x64]);  // ISO \
        Assert.False(map.UsageToLamp.ContainsKey(0x0F)); // the raw binding is not a usage
        Assert.Equal(122, map.KeyIdToLamp[55]);    // Fn via key id only
        Assert.Equal("Fn", map.Describe(122));
        Assert.Equal([0], map.UnmappedLamps);
        Assert.Equal("Edge light 1", map.Describe(0));
        Assert.True(map.TryGetLamp(0x29, out int esc) && esc == 26);
    }

    [Fact]
    public void LampMap_StandardHidBindings()
    {
        var lamps = new[] { Lamp(0, 0x29), Lamp(1, 0x2B), Lamp(2, 0x68), Lamp(3, 0) };
        Assert.Equal(LampBindingKind.HidKeyboardUsage, LampMap.DetectBindingKind(lamps));

        var map = LampMap.Build(lamps);
        Assert.Equal(0, map.UsageToLamp[0x29]);
        Assert.Equal("Tab", map.Describe(1));
        Assert.Equal("Usage 0x68", map.Describe(2));
        Assert.Equal([3], map.UnmappedLamps);
        Assert.Equal(87, map.Keys[0].KeyId);
    }

    [Fact]
    public void LampMap_FallsBackToPositions()
    {
        // Lamps without bindings at the ANSI key centres, rescaled to micrometres, shifted, lightly jittered and shuffled.
        var keys = DarkmountKeys.AnsiMainBlock;
        var rng = new Random(7);
        var lamps = keys
            .Select((k, i) => (Key: k, Lamp: Lamp(i, 0, (int)(50_000 + k.X * 173 + rng.Next(-300, 300)), (int)(k.Y * 173 + rng.Next(-300, 300)))))
            .OrderBy(_ => rng.Next())
            .ToList();

        Assert.Equal(LampBindingKind.None, LampMap.DetectBindingKind(lamps.Select(l => l.Lamp).ToList()));
        var map = LampMap.Build(lamps.Select(l => l.Lamp).ToList());

        Assert.Equal(keys.Count, map.Keys.Count);
        Assert.All(lamps, l =>
        {
            Assert.Equal(l.Key.Name, map.Keys[l.Lamp.Id].Name);
            Assert.Equal(LampKeySource.Position, map.Keys[l.Lamp.Id].Source);
        });
        Assert.Equal(lamps.Single(l => l.Key.Name == "Esc").Lamp.Id, map.UsageToLamp[0x29]);
    }

    [Fact]
    public void DynamicLighting_DeviceKeyNameFromHidPath()
    {
        Assert.Equal("hid#vid_373f&pid_0001&mi_03#c&31f00ac8&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
            DynamicLighting.DeviceKeyName(@"\\?\hid#vid_373f&pid_0001&mi_03#c&31f00ac8&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
        Assert.Equal("hid#x", DynamicLighting.DeviceKeyName(@"\\?\hid#x\kbd"));
        Assert.Null(DynamicLighting.DeviceKeyName(null));
        Assert.True(new DynamicLighting(true, null).WindowsMayDrive);
        Assert.False(new DynamicLighting(true, false).WindowsMayDrive);
        Assert.False(new DynamicLighting(false, true).WindowsMayDrive);
    }
}
