using Darkmount.Screens;
using Darkmount.Sensors;
using SkiaSharp;

namespace Darkmount.Tests;

public class ScreenRenderTests
{
    internal static string SnapshotDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Darkmount.Tests.csproj"))) dir = dir.Parent;
            var path = Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "snapshots");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    internal static void Save(SKBitmap bitmap, string name)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(SnapshotDir, name + ".png"), data.ToArray());
    }

    internal static void AssertValidFrame(SKBitmap bitmap)
    {
        Assert.Equal(320, bitmap.Width);
        Assert.Equal(240, bitmap.Height);
        var first = bitmap.GetPixel(0, 0);
        bool varied = false;
        for (int y = 0; y < bitmap.Height && !varied; y += 3)
            for (int x = 0; x < bitmap.Width && !varied; x += 3)
                varied = bitmap.GetPixel(x, y) != first;
        Assert.True(varied, "frame is a single uniform colour");
    }

    private static readonly Snapshot Typical = new()
    {
        CpuTemp = 72, CpuLoad = 45, CpuPower = 88,
        GpuTemp = 64, GpuLoad = 97, GpuPower = 280,
        RamUsedMb = 18200, RamTotalMb = 64670,
        VramUsedMb = 6100, VramTotalMb = 16280,
    };

    private static MetricHistory Waves(bool game)
    {
        var h = new MetricHistory();
        for (int i = 0; i < 60; i++)
        {
            double t = i / 59.0;
            h.Add(new Snapshot
            {
                CpuTemp = 62 + 8 * Math.Sin(i * 0.25) + 4 * Math.Sin(i * 0.7) + 2 * t,
                CpuLoad = 40 + 22 * Math.Sin(i * 0.18 + 1) + 10 * Math.Sin(i * 0.9),
                GpuTemp = 55 + 9 * t + 3 * Math.Sin(i * 0.4),
                GpuLoad = Math.Clamp(80 + 25 * Math.Sin(i * 0.12) + 6 * Math.Sin(i * 1.3), 0, 100),
                Fps = game ? 138 + 14 * Math.Sin(i * 0.3) + 6 * Math.Sin(i * 1.1) - (i == 37 ? 40 : 0) : null,
                FpsLow = game ? 97 : null,
                GameName = game ? "Cyberpunk2077.exe" : null,
            });
        }
        return h;
    }

    private static SKBitmap Render(ScreenContext ctx, string name)
    {
        var bitmap = DockRenderer.Render(new StatsScreen(), ctx);
        Save(bitmap, name);
        return bitmap;
    }

    private static MetricHistory WavesWithPower(bool game)
    {
        var h = new MetricHistory();
        for (int i = 0; i < 60; i++)
            h.Add(new Snapshot
            {
                CpuTemp = 62 + 8 * Math.Sin(i * 0.25), CpuLoad = 40 + 22 * Math.Sin(i * 0.18 + 1), CpuPower = 60 + 25 * Math.Sin(i * 0.3),
                GpuTemp = 58 + 6 * Math.Sin(i * 0.2), GpuLoad = Math.Clamp(80 + 25 * Math.Sin(i * 0.12), 0, 100), GpuPower = 230 + 40 * Math.Sin(i * 0.15),
                Fps = game ? 140 + 10 * Math.Sin(i * 0.3) : null, GameName = game ? "Game.exe" : null,
            });
        return h;
    }

    private static SKBitmap Render(StatsLayout layout, ScreenContext ctx, string name)
    {
        var bitmap = DockRenderer.Render(new StatsScreen { Layout = layout }, ctx);
        Save(bitmap, name);
        return bitmap;
    }

    [Fact]
    public void Layout_with_load_as_the_big_number_and_a_load_only_graph() // GitHub issue #2
    {
        var layout = new StatsLayout { Primary = StatsValue.Load, GraphLoad = true, GraphTemperature = false };
        using var desk = Render(layout, new ScreenContext { Snapshot = Typical, History = WavesWithPower(false) }, "stats-layout-load");
        using var game = Render(layout, new ScreenContext
        {
            Snapshot = Typical with { Fps = 144, FpsLow = 97, GameName = "Game.exe" }, History = WavesWithPower(true),
        }, "stats-layout-load-game");
        AssertValidFrame(desk);
        AssertValidFrame(game);
    }

    [Fact]
    public void Layout_with_watts_as_the_big_number_and_a_power_graph()
    {
        var layout = new StatsLayout { Primary = StatsValue.Power, GraphLoad = false, GraphTemperature = false, GraphPower = true };
        using var bmp = Render(layout, new ScreenContext { Snapshot = Typical, History = WavesWithPower(false) }, "stats-layout-power");
        AssertValidFrame(bmp);
    }

    [Fact]
    public void Missing_cpu_temperature_says_what_provides_it()
    {
        // No monitoring app: Windows provides CPU load and the GPU values, but not CPU temperature or watts.
        var windowsOnly = Typical with { CpuTemp = null, CpuPower = null };
        using var bmp = Render(new StatsLayout(), new ScreenContext { Snapshot = windowsOnly, History = Waves(false) }, "stats-no-cputemp");
        AssertValidFrame(bmp);
    }

    [Fact]
    public void AllNull_RendersPlaceholders()
    {
        using var bmp = Render(new ScreenContext { Snapshot = new Snapshot(), History = new MetricHistory() }, "stats-null");
        AssertValidFrame(bmp);
    }

    [Fact]
    public void NoGame_TypicalValues()
    {
        using var bmp = Render(new ScreenContext { Snapshot = Typical, History = Waves(false) }, "stats-nogame");
        AssertValidFrame(bmp);
    }

    [Fact]
    public void NoGame_HotAndFull()
    {
        var hot = Typical with { CpuTemp = 93, GpuTemp = 84, RamUsedMb = 58000, VramUsedMb = 15800, CpuLoad = 100, GpuPower = 1234 };
        using var bmp = Render(new ScreenContext { Snapshot = hot, History = Waves(false) }, "stats-hot");
        AssertValidFrame(bmp);
    }

    [Fact]
    public void InGame_ShowsFpsRow()
    {
        var s = Typical with { Fps = 144, FpsLow = 97, FpsLowLabel = "1% low", GameName = "Cyberpunk2077.exe" };
        using var bmp = Render(new ScreenContext { Snapshot = s, History = Waves(true) }, "stats-game");
        AssertValidFrame(bmp);
    }

    [Fact]
    public void InGame_AllNull()
    {
        var s = new Snapshot { GameName = "SomeVeryLongGameExecutableNameThatNeedsTruncation.exe" };
        using var bmp = Render(new ScreenContext { Snapshot = s, History = new MetricHistory() }, "stats-game-null");
        AssertValidFrame(bmp);
    }

    [Fact]
    public void TwoAlerts_DrawBanner()
    {
        var s = Typical with { GpuTemp = 91, CpuTemp = 93 };
        var ctx = new ScreenContext
        {
            Snapshot = s,
            History = Waves(false),
            Alerts = [new Alert("cpu-temp", "CPU 93°C"), new Alert("gpu-temp", "GPU 91°C")],
        };
        using var bmp = Render(ctx, "stats-alert");
        AssertValidFrame(bmp);
        // Banner area is red.
        var px = bmp.GetPixel(300, 18);
        Assert.True(px.Red > 180 && px.Green < 110 && px.Blue < 110, $"banner pixel {px}");
    }

    [Fact]
    public void ManyAlerts_ShrinkToFit()
    {
        var ctx = new ScreenContext
        {
            Snapshot = Typical,
            History = Waves(false),
            Alerts =
            [
                new Alert("cpu-temp", "CPU 93°C"), new Alert("gpu-temp", "GPU 91°C"),
                new Alert("ram", "RAM 92%"), new Alert("fps", "FPS 24"),
            ],
        };
        using var bmp = Render(ctx, "stats-alert-many");
        AssertValidFrame(bmp);
    }

    [Fact]
    public void InGame_WithAlert()
    {
        var s = Typical with { Fps = 24, FpsLow = 11, GameName = "Cyberpunk2077.exe", GpuTemp = 91 };
        var ctx = new ScreenContext
        {
            Snapshot = s,
            History = Waves(true),
            Alerts = [new Alert("gpu-temp", "GPU 91°C"), new Alert("fps", "FPS 24")],
        };
        using var bmp = Render(ctx, "stats-game-alert");
        AssertValidFrame(bmp);
    }

    [Fact]
    public void InGame_WithAlert_AllNull()
    {
        var ctx = new ScreenContext
        {
            Snapshot = new Snapshot { GameName = "game.exe" },
            History = new MetricHistory(),
            Alerts = [new Alert("fps", "FPS --")],
        };
        using var bmp = Render(ctx, "stats-game-alert-null");
        AssertValidFrame(bmp);
    }

    /// <summary>With alerts the stats rows must leave the banner area empty, so the banner hides nothing.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithAlerts_StatsRowsStayBelowBanner(bool inGame)
    {
        var s = inGame ? Typical with { Fps = 144, FpsLow = 97, GameName = "x.exe" } : Typical;
        var ctx = new ScreenContext { Snapshot = s, History = Waves(inGame), Alerts = [new Alert("k", "Alert")] };

        // Render the screen alone (no overlay) and check what the banner would cover.
        using var bmp = new SKBitmap(new SKImageInfo(320, 240, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(Theme.Background);
            new StatsScreen().Render(canvas, ctx);
        }
        for (int y = 0; y < (int)AlertOverlay.BannerHeight; y++)
            for (int x = 0; x < 320; x++)
                Assert.True(bmp.GetPixel(x, y) == Theme.Background, $"content under the banner at ({x},{y})");

        // Rows fill the remaining area down to the bottom margin (content reaches y ≥ 230, nothing past 240).
        bool bottomContent = false;
        for (int x = 0; x < 320 && !bottomContent; x++) bottomContent = bmp.GetPixel(x, 232) != Theme.Background;
        Assert.True(bottomContent, "last row does not reach the bottom of the screen");
    }

    [Fact]
    public void NoAlerts_OverlayIsNoOp()
    {
        using var a = new SKBitmap(320, 240);
        using var canvas = new SKCanvas(a);
        canvas.Clear(SKColors.Black);
        AlertOverlay.Draw(canvas, []);
        Assert.Equal(SKColors.Black, a.GetPixel(160, 18));
    }

    [Fact]
    public void History_KeepsLast60_OldestFirst()
    {
        var h = new MetricHistory();
        for (int i = 0; i < 75; i++) h.Add(new Snapshot { CpuTemp = i, Fps = i });
        Assert.Equal(60, h.CpuTemp.Count);
        Assert.Equal(15, h.CpuTemp[0]);
        Assert.Equal(74, h.CpuTemp[^1]);
        h.ClearFps();
        Assert.Empty(h.Fps);
        Assert.Equal(60, h.CpuTemp.Count);
    }

    [Fact]
    public void DockRenderer_ProducesOpaqueRgba()
    {
        using var bmp = DockRenderer.Render(new StatsScreen(), new ScreenContext { Snapshot = Typical, History = new MetricHistory() });
        Assert.Equal(SKColorType.Rgba8888, bmp.ColorType);
        Assert.Equal(SKAlphaType.Opaque, bmp.AlphaType);
        Assert.Equal(DockRenderer.Width, bmp.Width);
        Assert.Equal(DockRenderer.Height, bmp.Height);
    }
}
