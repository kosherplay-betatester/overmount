namespace Darkmount.Keyboard.Lamps;

/// <summary>
/// Pretend inputs for previews (the lighting studio without a keyboard, screenshots): someone typing "overmount rocks"
/// (with a typo fixed by Backspace, then Enter) at six keys a second, music at 120 bpm, the mouse drawing figure-eights
/// and clicking, a screen with a drifting picture and a flash every few seconds, warm sensors, and a whole day passing
/// every 30 seconds. Every value depends only on the time, like the effects.
/// </summary>
public static class SceneDemo
{
    static readonly int[] Typing = "overmount rockz\bs\n".Select(c => c switch
        {
            ' ' => Usage(0x2C),
            '\b' => Usage(0x2A),
            '\n' => Usage(0x28),
            _ => KeyIds.All.FirstOrDefault(k => k.Zone == KeyZone.Keyboard && k.Label.Equals(c.ToString(), StringComparison.OrdinalIgnoreCase))?.Id ?? 57,
        }).Select(id => (int)id).ToArray();

    static byte Usage(int usage) => KeyIds.All.FirstOrDefault(k => k.Zone == KeyZone.Keyboard && k.HidUsage == usage)?.Id ?? 57;

    const double KeysPerSecond = 6, BeatSeconds = 0.5;
    const int GridWidth = 24, GridHeight = 8;

    public static SceneContext Context(double seconds, IReadOnlyList<LampPoint> lamps)
    {
        var presses = new List<KeyPress>();
        var lastPress = new Dictionary<int, double>();
        for (int i = (int)Math.Floor((seconds - 4) * KeysPerSecond); i <= (int)Math.Floor(seconds * KeysPerSecond); i++)
        {
            double at = i / KeysPerSecond;
            if (at < 0 || at > seconds) continue;
            int key = Typing[((i % Typing.Length) + Typing.Length) % Typing.Length];
            presses.Add(new KeyPress(key, at));
            lastPress[key] = at;
        }

        var beats = new List<double>();
        for (double b = Math.Floor(seconds / BeatSeconds) * BeatSeconds; b > seconds - 3 && b >= 0; b -= BeatSeconds) beats.Insert(0, b);
        double sinceBeat = seconds - (beats.Count > 0 ? beats[^1] : -1);
        double kick = Math.Exp(-sinceBeat * 6);
        var bands = Enumerable.Range(0, 16)
            .Select(i => Math.Clamp((i < 3 ? kick : 0.35) * (0.6 + 0.4 * Math.Sin(seconds * (1.3 + i * 0.37) + i)) + 0.1, 0, 1)).ToArray();

        var clicks = new List<MouseClick>();
        for (int c = (int)Math.Floor(seconds / 1.3); c >= 0 && c >= (int)Math.Floor(seconds / 1.3) - 1; c--)
        {
            double at = c * 1.3;
            clicks.Insert(0, new MouseClick(MouseX(at), MouseY(at), c % 3 == 2 ? 1 : 0, at));
        }

        var flashes = new List<ScreenFlash>();
        double flashAt = Math.Floor(seconds / 3.5) * 3.5;
        if (flashAt > 0) flashes.Add(new ScreenFlash(flashAt, new LampColor(255, 170, 60), 0.9));

        var grid = new LampColor[GridWidth * GridHeight];
        for (int y = 0; y < GridHeight; y++)
            for (int x = 0; x < GridWidth; x++)
                grid[y * GridWidth + x] = Rgbf.Hsv(seconds * 0.05 + x / (double)GridWidth * 0.3 + y * 0.02, 0.75, 0.55 + 0.35 * y / GridHeight).ToLamp();

        return new SceneContext
        {
            Seconds = seconds, Lamps = lamps,
            KeyPressTimes = lastPress, KeyPresses = presses,
            CpuTemp = 55 + 10 * Math.Sin(seconds * 0.2), CpuLoad = 40 + 25 * Math.Sin(seconds * 0.3), GpuTemp = 58, GpuLoad = 70,
            AudioLevel = Math.Clamp(0.35 + 0.55 * kick, 0, 1), AudioBands = bands, BeatTimes = beats,
            MouseX = MouseX(seconds), MouseY = MouseY(seconds), MouseClicks = clicks,
            ScreenGrid = grid, ScreenGridWidth = GridWidth, ScreenGridHeight = GridHeight, ScreenFlashes = flashes,
            LocalHours = seconds * 24 / 30 % 24,
        };
    }

    static double MouseX(double t) => 0.5 + 0.38 * Math.Sin(t * 0.7);
    static double MouseY(double t) => 0.5 + 0.35 * Math.Sin(t * 1.4);
}
