namespace Darkmount.Keyboard.Lamps.OpenRgb;

/// <summary>Turns an OpenRGB controller into lamp positions for the effects engine, and LED key names into HID usages.</summary>
public static class OpenRgbLayout
{
    /// <summary>
    /// One <see cref="LampPoint"/> per LED: LampId = LED index; X, Y normalised 0..1 over the device.
    /// Matrix-mapped LEDs sit at their (column, row) cell; every other zone is one row with its LEDs spread evenly left → right
    /// (a single LED at the centre). Rows stack in zone order. An LED is a key when its name starts with "Key:", it is in a
    /// matrix zone, or the device is keyboard-like and its zone name contains "keyboard". KeyId is the Dark Mount key id of
    /// the LED's HID usage, else 0.
    /// </summary>
    public static IReadOnlyList<LampPoint> Build(OpenRgbController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        var count = controller.LedNames.Count;
        if (count == 0) return [];

        var x = new double[count];
        var y = new double[count];
        var placed = new bool[count];
        var zoneKey = new bool[count];

        // Width that rows of non-matrix LEDs spread over: the widest matrix, or 0..1 when there is none.
        var span = Math.Max(1, controller.Zones.Where(HasMatrix).Select(z => z.MatrixWidth - 1).DefaultIfEmpty(1).Max());
        var row = 0.0;

        void PlaceRow(IReadOnlyList<int> leds)
        {
            for (var i = 0; i < leds.Count; i++)
            {
                var led = leds[i];
                x[led] = leds.Count == 1 ? span / 2.0 : span * i / (double)(leds.Count - 1);
                y[led] = row;
                placed[led] = true;
            }
            if (leds.Count > 0) row++;
        }

        foreach (var zone in controller.Zones)
        {
            var first = Math.Clamp(zone.StartLed, 0, count);
            var end = (int)Math.Clamp((long)zone.StartLed + Math.Max(0, zone.LedCount), first, count);
            var isKeyZone = zone.Type == OpenRgbZone.MatrixType
                || controller.IsKeyboardLike && zone.Name.Contains("keyboard", StringComparison.OrdinalIgnoreCase);
            for (var led = first; led < end; led++) zoneKey[led] |= isKeyZone;

            if (HasMatrix(zone))
            {
                for (var r = 0; r < zone.MatrixHeight; r++)
                for (var c = 0; c < zone.MatrixWidth; c++)
                {
                    var entry = zone.Matrix[r * zone.MatrixWidth + c];
                    if (entry == OpenRgbProtocol.NoLed || entry >= (uint)zone.LedCount) continue;
                    var led = first + (int)entry;
                    if (led >= end || placed[led]) continue;
                    (x[led], y[led], placed[led]) = (c, row + r, true);
                }
                row += zone.MatrixHeight;
            }

            // Zone LEDs without a matrix cell (or the whole zone when it has no matrix) form a row.
            PlaceRow(Enumerable.Range(first, end - first).Where(led => !placed[led]).ToList());
        }

        // LEDs no zone covers.
        PlaceRow(Enumerable.Range(0, count).Where(led => !placed[led]).ToList());

        var (minX, maxX) = (x.Min(), x.Max());
        var (minY, maxY) = (y.Min(), y.Max());
        var points = new LampPoint[count];
        for (var led = 0; led < count; led++)
        {
            var name = controller.LedNames[led] ?? "";
            var keyId = HidUsage(name) is { } usage && DarkmountKeys.ByUsage.TryGetValue(usage, out var key) ? key.Id : 0;
            var isKey = zoneKey[led] || name.TrimStart().StartsWith("Key:", StringComparison.OrdinalIgnoreCase);
            points[led] = new LampPoint(led, Normalise(x[led], minX, maxX), Normalise(y[led], minY, maxY), isKey, keyId);
        }
        return points;
    }

    static bool HasMatrix(OpenRgbZone z) =>
        z.MatrixHeight > 0 && z.MatrixWidth > 0 && z.Matrix.Count >= (long)z.MatrixHeight * z.MatrixWidth;

    static double Normalise(double v, double min, double max)
    {
        var range = max - min;
        if (!(range > 1e-12) || !double.IsFinite(range)) return 0.5;
        var n = (v - min) / range;
        return double.IsFinite(n) ? Math.Clamp(n, 0, 1) : 0.5;
    }

    /// <summary>"Key: A" → 0x04 (HID Keyboard-page usage); null when the LED isn't a known key (Fn included). Case-insensitive.</summary>
    public static int? HidUsage(string ledName)
    {
        if (string.IsNullOrWhiteSpace(ledName)) return null;
        var name = ledName.Trim();
        if (!name.StartsWith("Key:", StringComparison.OrdinalIgnoreCase)) return null;
        return Usages.TryGetValue(name[4..].Trim(), out var usage) ? usage : null;
    }

    static readonly Dictionary<string, int> Usages = BuildUsages();

    static Dictionary<string, int> BuildUsages()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 26; i++) map[((char)('A' + i)).ToString()] = 0x04 + i;
        for (var i = 1; i <= 9; i++) map[i.ToString()] = 0x1E + i - 1;
        map["0"] = 0x27;
        for (var i = 1; i <= 12; i++) map[$"F{i}"] = 0x3A + i - 1;
        for (var i = 13; i <= 24; i++) map[$"F{i}"] = 0x68 + i - 13;
        for (var i = 1; i <= 9; i++) map[$"Number Pad {i}"] = 0x59 + i - 1;
        map["Number Pad 0"] = 0x62;

        (string Name, int Usage)[] named =
        [
            ("Enter", 0x28), ("Enter (ISO)", 0x28), ("Enter (ANSI)", 0x28), ("Return", 0x28),
            ("Escape", 0x29), ("Esc", 0x29), ("Backspace", 0x2A), ("Tab", 0x2B), ("Space", 0x2C),
            ("-", 0x2D), ("=", 0x2E), ("[", 0x2F), ("]", 0x30), ("\\", 0x31), ("\\ (ANSI)", 0x31), ("#", 0x32),
            (";", 0x33), ("'", 0x34), ("`", 0x35), (",", 0x36), (".", 0x37), ("/", 0x38), ("Caps Lock", 0x39),
            ("Print Screen", 0x46), ("Scroll Lock", 0x47), ("Pause/Break", 0x48), ("Pause", 0x48),
            ("Insert", 0x49), ("Home", 0x4A), ("Page Up", 0x4B), ("Delete", 0x4C), ("End", 0x4D), ("Page Down", 0x4E),
            ("Right Arrow", 0x4F), ("Left Arrow", 0x50), ("Down Arrow", 0x51), ("Up Arrow", 0x52),
            ("Num Lock", 0x53), ("Number Pad /", 0x54), ("Number Pad *", 0x55), ("Number Pad -", 0x56),
            ("Number Pad +", 0x57), ("Number Pad Enter", 0x58), ("Number Pad .", 0x63),
            ("\\ (ISO)", 0x64), ("Menu", 0x65),
            ("Left Control", 0xE0), ("Left Ctrl", 0xE0), ("Left Shift", 0xE1), ("Left Alt", 0xE2),
            ("Left Windows", 0xE3), ("Left Win", 0xE3),
            ("Right Control", 0xE4), ("Right Ctrl", 0xE4), ("Right Shift", 0xE5), ("Right Alt", 0xE6),
            ("Right Windows", 0xE7), ("Right Win", 0xE7),
        ];
        foreach (var (name, usage) in named) map[name] = usage;
        return map;
    }
}
