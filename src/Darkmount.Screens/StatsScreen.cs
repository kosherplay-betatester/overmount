using System.Globalization;
using Darkmount.Sensors;
using SkiaSharp;

namespace Darkmount.Screens;

/// <summary>The CPU/GPU value shown big (the other two are shown small).</summary>
public enum StatsValue { Temperature, Load, Power }

/// <summary>How the CPU and GPU rows are arranged: which value is big and which lines the graph draws.</summary>
public sealed record StatsLayout
{
    public StatsValue Primary { get; init; } = StatsValue.Temperature;
    public bool GraphLoad { get; init; } = true;
    public bool GraphTemperature { get; init; } = true;
    public bool GraphPower { get; init; }
}

/// <summary>
/// The stats matrix. Without a game: CPU, GPU and MEM rows of 80 px. With a game: CPU 60, GPU 60, MEM 40 and
/// an 80 px FPS row. While alerts are active the rows are laid out below the 36 px alert banner.
/// </summary>
public sealed class StatsScreen : IDockScreen
{
    private const float Margin = 3;       // outer margin around each panel
    private const float LeftColumn = 138; // width of the numbers column inside CPU/GPU/FPS rows
    private const double TempMin = 30, TempMax = 100;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Name => "Stats";

    /// <summary>Which value is big and what the graphs show (Dock screen page → Stats layout).</summary>
    public StatsLayout Layout { get; set; } = new();

    /// <summary>One CPU or GPU row's values and their history.</summary>
    private readonly record struct Metrics(double? Temp, double? Load, double? Power,
        IReadOnlyList<double?> TempHistory, IReadOnlyList<double?> LoadHistory, IReadOnlyList<double?> PowerHistory);

    public void Render(SKCanvas canvas, ScreenContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var s = ctx.Snapshot;
        var h = ctx.History;

        // With an active alert the banner owns the top 36 px, so every row moves below it (nothing is hidden).
        bool alert = ctx.Alerts is { Count: > 0 };
        float start = alert ? AlertOverlay.BannerHeight : 0;
        float y = start;

        if (!s.InGame)
        {
            // No alert: 80 / 80 / 80. Alert (204 px): 80 / 80 / 44 - CPU/GPU keep full size, MEM goes compact.
            DrawChipRow(canvas, Next(80), "CPU", Theme.Cpu, Cpu(s, h), h.Capacity, compact: false);
            DrawChipRow(canvas, Next(80), "GPU", Theme.Gpu, Gpu(s, h), h.Capacity, compact: false);
            DrawMemRow(canvas, Next(alert ? 44 : 80), s, compact: alert);
        }
        else
        {
            // No alert: 60 / 60 / 40 / 80. Alert (204 px): 54 / 54 / 34 / 62 with a compact FPS row.
            float chip = alert ? 54 : 60;
            DrawChipRow(canvas, Next(chip), "CPU", Theme.Cpu, Cpu(s, h), h.Capacity, compact: true);
            DrawChipRow(canvas, Next(chip), "GPU", Theme.Gpu, Gpu(s, h), h.Capacity, compact: true);
            DrawMemRow(canvas, Next(alert ? 34 : 40), s, compact: true);
            DrawFpsRow(canvas, Next(alert ? 62 : 80), s, h, compact: alert);
        }

        // Rows are separated by Margin; the outer edges (screen or banner) get a full Margin too.
        SKRect Next(float height)
        {
            float top = y, bottom = Math.Min(y + height, DockRenderer.Height);
            y = bottom;
            return new SKRect(Margin, top + (top <= start ? Margin : Margin / 2), DockRenderer.Width - Margin,
                bottom - (bottom >= DockRenderer.Height ? Margin : Margin / 2));
        }
    }

    // ---------------------------------------------------------------- CPU / GPU

    private static Metrics Cpu(Snapshot s, MetricHistory h) => new(s.CpuTemp, s.CpuLoad, s.CpuPower, h.CpuTemp, h.CpuLoad, h.CpuPower);

    private static Metrics Gpu(Snapshot s, MetricHistory h) => new(s.GpuTemp, s.GpuLoad, s.GpuPower, h.GpuTemp, h.GpuLoad, h.GpuPower);

    private static double? ValueOf(Metrics m, StatsValue v) => v switch
    {
        StatsValue.Load => m.Load, StatsValue.Power => m.Power, _ => m.Temp,
    };

    private static string UnitOf(StatsValue v) => v switch { StatsValue.Load => "%", StatsValue.Power => "W", _ => "°C" };

    private void DrawChipRow(SKCanvas canvas, SKRect panel, string label, SKColor accent, Metrics m, int capacity, bool compact)
    {
        Theme.DrawPanel(canvas, panel);
        DrawAccentTab(canvas, panel, accent);

        float left = panel.Left + 11;
        float colRight = panel.Left + LeftColumn;
        var primary = Layout.Primary;
        // A missing value (CPU temperature without Afterburner) isn't worth the big spot: show the next one that exists.
        if (ValueOf(m, primary) is null && Enum.GetValues<StatsValue>().FirstOrDefault(v => ValueOf(m, v) is not null) is var available
            && ValueOf(m, available) is not null)
            primary = available;
        var secondary = Enum.GetValues<StatsValue>().Where(v => v != primary).ToArray(); // in Temperature, Load, Power order
        double? big = ValueOf(m, primary);

        using var labelFont = Theme.Font(Theme.Bold, compact ? 11 : 12.5f);
        using var accentPaint = Theme.Fill(accent);

        if (!compact)
        {
            // CPU                  (label)
            // 72°C                 (big value)
            // 45 %  ·  88 W        (the other two)
            float labelBaseline = panel.Top + 16;
            canvas.DrawText(label, left, labelBaseline, SKTextAlign.Left, labelFont, accentPaint);
            // Windows can't read CPU temperature without a monitoring app: say what would provide it.
            if (m.Temp is null && label == "CPU")
            {
                using var hintFont = Theme.Font(Theme.SemiBold, 9.5f);
                using var hintPaint = Theme.Fill(Theme.TextDim);
                float hx = left + labelFont.MeasureText(label) + 7;
                canvas.DrawText(Theme.Ellipsize("needs Afterburner", hintFont, colRight - 6 - hx), hx, labelBaseline, SKTextAlign.Left, hintFont, hintPaint);
            }
            DrawBig(canvas, left, panel.Top + 54, 47, big, primary, accent);
            TextRuns.Draw(canvas, left, panel.Top + 70,
                Value(ValueOf(m, secondary[0]), "0"), Theme.Font(Theme.SemiBold, 14), Num(ValueOf(m, secondary[0])),
                " " + UnitOf(secondary[0]), Theme.Font(Theme.SemiBold, 11.5f), Theme.TextDim,
                "  •  ", Theme.Font(Theme.Bold, 11f), Theme.TextDim,
                Value(ValueOf(m, secondary[1]), "0"), Theme.Font(Theme.SemiBold, 14), Num(ValueOf(m, secondary[1])),
                " " + UnitOf(secondary[1]), Theme.Font(Theme.SemiBold, 11.5f), Theme.TextDim);
        }
        else
        {
            // CPU          45 %
            // 72°C         88 W
            float size = CompactTempSize(panel), baseline = CompactBaseline(panel);
            canvas.DrawText(label, left, panel.Top + 13.5f, SKTextAlign.Left, labelFont, accentPaint);
            DrawBig(canvas, left, baseline, size, big, primary, accent);
            TextRuns.DrawRight(canvas, colRight - 6, baseline - 20,
                Value(ValueOf(m, secondary[0]), "0"), Theme.Font(Theme.SemiBold, 14), Num(ValueOf(m, secondary[0])),
                " " + UnitOf(secondary[0]), Theme.Font(Theme.SemiBold, 11), Theme.TextDim);
            TextRuns.DrawRight(canvas, colRight - 6, baseline - 2,
                Value(ValueOf(m, secondary[1]), "0"), Theme.Font(Theme.SemiBold, 14), Num(ValueOf(m, secondary[1])),
                " " + UnitOf(secondary[1]), Theme.Font(Theme.SemiBold, 11), Theme.TextDim);
        }

        var graph = new SKRect(colRight, panel.Top + 5, panel.Right - 5, panel.Bottom - 5);
        DrawGraphFrame(canvas, graph);
        var inner = Inset(graph);
        // Load is the filled area; temperature and watts are lines. With load off, the first chosen series is filled.
        bool fillDone = false;
        if (Layout.GraphLoad)
        {
            WaveGraph.Draw(canvas, inner, m.LoadHistory, 0, 100, accent, fill: true, capacity);
            fillDone = true;
        }
        if (Layout.GraphTemperature)
        {
            WaveGraph.Draw(canvas, inner, m.TempHistory, TempMin, TempMax, accent, fill: !fillDone, capacity);
            fillDone = true;
        }
        if (Layout.GraphPower)
        {
            double peak = 0;
            foreach (var v in m.PowerHistory) if (v is double d && d > peak) peak = d;
            if (m.Power is double now && now > peak) peak = now;
            WaveGraph.Draw(canvas, inner, m.PowerHistory, 0, Math.Max(50, peak * 1.2), Lighten(accent, 0.35f), fill: !fillDone, capacity);
        }
    }

    /// <summary>Temperature size for compact rows: ~36 px in a 54 px panel, never below 28.</summary>
    private static float CompactTempSize(SKRect panel) => Math.Clamp(panel.Height * 0.68f, 28, 38);

    private static float CompactBaseline(SKRect panel) => panel.Bottom - Math.Max(5.5f, panel.Height * 0.12f);

    /// <summary>
    /// The big value with its unit as a superscript. Temperature is coloured by how hot it is; load and watts take the
    /// row's colour (a busy GPU in a game is good news, not an alarm).
    /// </summary>
    private static void DrawBig(SKCanvas canvas, float x, float baseline, float size, double? value, StatsValue kind, SKColor accent)
    {
        using var big = Theme.Font(Theme.Bold, size);
        var color = value is null ? Theme.TextDim
            : kind == StatsValue.Temperature ? Theme.TempColor(value) : Lighten(accent, 0.25f);
        using var paint = Theme.Fill(color);
        string text = Value(value, "0");
        canvas.DrawText(text, x, baseline, SKTextAlign.Left, big, paint);
        float adv = big.MeasureText(text);

        // The unit as a superscript: its cap top aligned with the number's cap top.
        using var unit = Theme.Font(Theme.SemiBold, size * 0.40f);
        using var unitPaint = Theme.Fill(value is null ? Theme.TextDim : Theme.TextSecondary);
        float capTop = baseline - big.Metrics.CapHeight;
        float unitBaseline = capTop + unit.Metrics.CapHeight;
        canvas.DrawText(UnitOf(kind), x + adv + size * 0.05f, unitBaseline, SKTextAlign.Left, unit, unitPaint);
    }

    // ---------------------------------------------------------------- MEM

    private static void DrawMemRow(SKCanvas canvas, SKRect panel, Snapshot s, bool compact)
    {
        Theme.DrawPanel(canvas, panel);
        DrawAccentTab(canvas, panel, Theme.Mem);

        if (!compact)
        {
            float left = panel.Left + 11, right = panel.Right - 11;
            DrawMemBar(canvas, left, right, panel.Top + 19, 8, "RAM", s.RamUsedMb, s.RamTotalMb, 12.5f, 15);
            DrawMemBar(canvas, left, right, panel.Top + 53, 8, "VRAM", s.VramUsedMb, s.VramTotalMb, 12.5f, 15);
        }
        else
        {
            // Label/value line + thin bar, side by side, centred vertically in the panel.
            bool roomy = panel.Height >= 38;
            float labelSize = roomy ? 11.5f : 10.5f, valueSize = roomy ? 14 : 12.5f, bar = roomy ? 6 : 5;
            float capH = valueSize * 0.7f, content = capH + 6 + bar;
            float baseline = panel.Top + (panel.Height - content) / 2 + capH;
            float mid = panel.MidX;
            DrawMemBar(canvas, panel.Left + 11, mid - 8, baseline, bar, "RAM", s.RamUsedMb, s.RamTotalMb, labelSize, valueSize);
            DrawMemBar(canvas, mid + 8, panel.Right - 9, baseline, bar, "VRAM", s.VramUsedMb, s.VramTotalMb, labelSize, valueSize);
        }
    }

    private static void DrawMemBar(SKCanvas canvas, float left, float right, float baseline, float barHeight,
        string label, double? usedMb, double? totalMb, float labelSize, float valueSize)
    {
        using var labelFont = Theme.Font(Theme.Bold, labelSize);
        using var labelPaint = Theme.Fill(Theme.Mem);
        canvas.DrawText(label, left, baseline, SKTextAlign.Left, labelFont, labelPaint);

        string used = usedMb is double u ? (u / 1024).ToString("0.0", Inv) : "--";
        string total = totalMb is double t ? (t / 1024).ToString("0.0", Inv) : "--";
        TextRuns.DrawRight(canvas, right, baseline,
            used, Theme.Font(Theme.Bold, valueSize), usedMb is null ? Theme.TextDim : Theme.Text,
            " / " + total, Theme.Font(Theme.SemiBold, valueSize * 0.82f), Theme.TextSecondary,
            " GB", Theme.Font(Theme.SemiBold, valueSize * 0.72f), Theme.TextDim);

        var track = new SKRect(left, baseline + 6, right, baseline + 6 + barHeight);
        float radius = barHeight / 2;
        using (var trackPaint = Theme.Fill(Theme.Track))
            canvas.DrawRoundRect(track, radius, radius, trackPaint);

        if (usedMb is double used2 && totalMb is double total2 && total2 > 0)
        {
            double pct = Math.Clamp(used2 / total2 * 100, 0, 100);
            var color = Theme.MemColor(pct);
            float fillRight = track.Left + (float)(track.Width * pct / 100);
            fillRight = Math.Max(fillRight, track.Left + barHeight); // always show a rounded nub
            var bar = new SKRect(track.Left, track.Top, fillRight, track.Bottom);
            using var fill = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(new SKPoint(bar.Left, 0), new SKPoint(bar.Right, 0),
                    [Blend(color, SKColors.Black, 0.35f), color], SKShaderTileMode.Clamp),
            };
            canvas.DrawRoundRect(bar, radius, radius, fill);
        }
    }

    // ---------------------------------------------------------------- FPS

    private static void DrawFpsRow(SKCanvas canvas, SKRect panel, Snapshot s, MetricHistory h, bool compact)
    {
        Theme.DrawPanel(canvas, panel);
        DrawAccentTab(canvas, panel, Theme.Fps);

        float left = panel.Left + 11;
        float colRight = panel.Left + LeftColumn;

        // Header: "FPS" + game name
        float headerBaseline = panel.Top + (compact ? 14 : 16);
        using var labelFont = Theme.Font(Theme.Bold, compact ? 11.5f : 12.5f);
        using var accent = Theme.Fill(Theme.Fps);
        canvas.DrawText("FPS", left, headerBaseline, SKTextAlign.Left, labelFont, accent);
        float labelW = labelFont.MeasureText("FPS");
        if (s.GameName is { Length: > 0 } game)
        {
            using var gameFont = Theme.Font(Theme.SemiBold, 11);
            using var gamePaint = Theme.Fill(Theme.TextSecondary);
            string name = StripExe(game);
            float gx = left + labelW + 7;
            name = Theme.Ellipsize(name, gameFont, colRight - 6 - gx);
            canvas.DrawText(name, gx, headerBaseline, SKTextAlign.Left, gameFont, gamePaint);
        }

        using var bigPaint = Theme.Fill(s.Fps is null ? Theme.TextDim : Lighten(Theme.Fps, 0.25f));
        string fps = Value(s.Fps, "0");
        if (!compact)
        {
            // Big average FPS, low value underneath.
            using var big = Theme.Font(Theme.Bold, 47);
            canvas.DrawText(fps, left, panel.Top + 54, SKTextAlign.Left, big, bigPaint);
            TextRuns.Draw(canvas, left, panel.Top + 70,
                s.FpsLowLabel + "  ", Theme.Font(Theme.SemiBold, 11.5f), Theme.TextDim,
                Value(s.FpsLow, "0"), Theme.Font(Theme.SemiBold, 14), s.FpsLow is null ? Theme.TextDim : Theme.Text);
        }
        else
        {
            // Same arrangement as a compact CPU/GPU row: number left, low label/value stacked right.
            // The number is never larger than the temperatures above it (same size as a 51 px chip panel).
            float size = CompactTempSize(new SKRect(0, 0, 1, 51));
            using var big = Theme.Font(Theme.Bold, size);
            float room = panel.Bottom - 6 - (headerBaseline + 4);
            float baseline = headerBaseline + 4 + (room + big.Metrics.CapHeight) / 2;
            canvas.DrawText(fps, left, baseline, SKTextAlign.Left, big, bigPaint);
            TextRuns.DrawRight(canvas, colRight - 6, baseline - 19,
                s.FpsLowLabel, Theme.Font(Theme.SemiBold, 10.5f), Theme.TextDim);
            TextRuns.DrawRight(canvas, colRight - 6, baseline - 1,
                Value(s.FpsLow, "0"), Theme.Font(Theme.SemiBold, 14), s.FpsLow is null ? Theme.TextDim : Theme.Text);
        }

        // Graph
        var graph = new SKRect(colRight, panel.Top + 5, panel.Right - 5, panel.Bottom - 5);
        DrawGraphFrame(canvas, graph);
        double peak = 0;
        foreach (var v in h.Fps) if (v is double d && d > peak) peak = d;
        if (s.Fps is double cur && cur > peak) peak = cur;
        double max = Math.Max(60, peak * 1.2);
        var inner = Inset(graph);
        WaveGraph.Draw(canvas, inner, h.Fps, 0, max, Theme.Fps, fill: true, h.Capacity);
        if (s.FpsLow is double low)
            WaveGraph.DrawDashedLevel(canvas, inner, low, 0, max, Lighten(Theme.Fps, 0.45f).WithAlpha(0xD0));
        WaveGraph.Draw(canvas, inner, h.Fps, 0, max, Theme.Fps, fill: false, h.Capacity);
    }

    // ---------------------------------------------------------------- helpers

    private static void DrawAccentTab(SKCanvas canvas, SKRect panel, SKColor accent)
    {
        // A short accent strip on the panel's left edge ties the row to its colour.
        var tab = new SKRect(panel.Left, panel.Top + 10, panel.Left + 3, panel.Bottom - 10);
        using var paint = Theme.Fill(accent);
        canvas.DrawRoundRect(tab, 1.5f, 1.5f, paint);
    }

    private static void DrawGraphFrame(SKCanvas canvas, SKRect graph)
    {
        using var bg = Theme.Fill(Theme.GraphBackground);
        canvas.DrawRoundRect(graph, 5, 5, bg);
        WaveGraph.DrawGrid(canvas, graph, 4);
    }

    private static SKRect Inset(SKRect r) => new(r.Left + 1, r.Top + 2, r.Right - 1, r.Bottom - 1);

    private static string Value(double? v, string format) =>
        v is double d && !double.IsNaN(d) && !double.IsInfinity(d) ? d.ToString(format, Inv) : "--";

    private static SKColor Num(double? v) => v is null ? Theme.TextDim : Theme.Text;

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static SKColor Blend(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t), a.Alpha);

    private static SKColor Lighten(SKColor c, float t) => Blend(c, SKColors.White, t);
}

/// <summary>Draws a line made of differently styled runs (fonts are disposed after drawing).</summary>
internal static class TextRuns
{
    /// <summary>Arguments come in triples: string, SKFont, SKColor.</summary>
    public static float Draw(SKCanvas canvas, float x, float baseline, params object[] runs)
    {
        float cx = x;
        for (int i = 0; i + 2 < runs.Length; i += 3)
        {
            var text = (string)runs[i];
            var font = (SKFont)runs[i + 1];
            var color = (SKColor)runs[i + 2];
            using var paint = Theme.Fill(color);
            canvas.DrawText(text, cx, baseline, SKTextAlign.Left, font, paint);
            cx += font.MeasureText(text);
            font.Dispose();
        }
        return cx - x;
    }

    public static float DrawRight(SKCanvas canvas, float right, float baseline, params object[] runs)
    {
        float width = 0;
        for (int i = 0; i + 2 < runs.Length; i += 3)
            width += ((SKFont)runs[i + 1]).MeasureText((string)runs[i]);
        return Draw(canvas, right - width, baseline, runs);
    }
}
