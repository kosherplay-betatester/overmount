using System.Drawing.Drawing2D;
using Darkmount.Keyboard;

namespace Darkmount.App.Pages;

/// <summary>
/// The keyboard's built-in lighting effects (layer 0): effect, colours, direction, brightness, speed. Changes are written
/// to the keyboard as you make them (after a short pause) and make the built-in effect the active lighting.
/// </summary>
public sealed class LightingPage : Ui.Page
{
    readonly KeyboardService _keyboard;
    readonly Action? _beforeApply;
    readonly System.Windows.Forms.Timer _applyTimer = new() { Interval = 350 };
    readonly FlowLayoutPanel _effects = new() { AutoSize = true, WrapContents = true, MaximumSize = new Size(640, 0) };
    readonly ComboBox _colorMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Font = Ui.Body };
    readonly FlowLayoutPanel _colors = new() { AutoSize = true, WrapContents = false };
    readonly Button _addColor, _removeColor;
    readonly ComboBox _direction = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Font = Ui.Body };
    readonly TrackBar _brightness = Slider(), _speed = Slider();
    readonly Label _brightnessValue = Value(), _speedValue = Value();
    readonly Panel _preview = new() { Size = new Size(420, 26), Margin = new Padding(0, 6, 0, 6) };
    readonly Label _status = Ui.Note("", 640);
    readonly Control _directionRow, _speedRow;
    readonly Dictionary<Effect, Button> _effectButtons = [];
    Effect _effect = Effect.Static;
    bool _loading;
    int _edits; // counts edits, so a keyboard read that finishes after one doesn't overwrite it

    /// <param name="beforeApply">Called before a change is written (the host hands the LEDs to the built-in effect).</param>
    public LightingPage(KeyboardService keyboard, Action? beforeApply = null)
        : base("Built-in effect", "Runs on the keyboard itself, so it keeps going even when OverMount is closed. Changes " +
                                  "are saved to the keyboard as you make them; your original lighting is backed up first.")
    {
        _keyboard = keyboard;
        _beforeApply = beforeApply;
        _applyTimer.Tick += async (_, _) => { _applyTimer.Stop(); await Apply(); };
        Disposed += (_, _) => _applyTimer.Dispose();
        foreach (var info in LightingEffects.DarkMount)
        {
            var b = Ui.Button(info.Name, (_, _) => SelectEffect(info.Effect));
            b.MinimumSize = new Size(110, 36);
            _effectButtons[info.Effect] = b;
            _effects.Controls.Add(b);
        }
        _addColor = Ui.Button("+", (_, _) => AddColor());
        _removeColor = Ui.Button("−", (_, _) => RemoveColor());
        _addColor.MinimumSize = _removeColor.MinimumSize = new Size(36, 30);
        _colorMode.SelectedIndexChanged += (_, _) => { if (!_loading) { ResetColors(); Changed(); } };
        _direction.SelectedIndexChanged += (_, _) => Changed();
        _brightness.ValueChanged += (_, _) => { _brightnessValue.Text = $"{_brightness.Value}%"; Changed(); };
        _speed.ValueChanged += (_, _) => { _speedValue.Text = $"{_speed.Value}%"; Changed(); };
        _preview.Paint += PaintPreview;

        Row("Effect", _effects);
        Row("Colours", _colorMode);
        var colorRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        colorRow.Controls.AddRange([_colors, _addColor, _removeColor]);
        Row("", colorRow);
        Row("Preview", _preview);
        _directionRow = Pair(_direction, null);
        Row("Direction", _directionRow);
        Row("Brightness", Pair(_brightness, _brightnessValue));
        _speedRow = Pair(_speed, _speedValue);
        Row("Speed", _speedRow);

        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        actions.Controls.Add(Ui.Button("Read from keyboard", async (_, _) => await LoadFromKeyboard()));
        actions.Controls.Add(Ui.Button("be quiet! factory lighting", (_, _) => { Show(LightingMode.General, LightingEffects.FactoryDefault); Changed(); }));
        actions.Controls.Add(Ui.Button("Restore my original lighting", (_, _) => RestoreOriginal()));
        AddFull(actions);
        AddFull(_status);

        Show(LightingMode.General, LightingEffects.FactoryDefault);
        VisibleChanged += async (_, _) => { if (Visible) await LoadFromKeyboard(); };
    }

    static TrackBar Slider() => new()
    {
        Minimum = LightingEffects.MinBrightness, Maximum = LightingEffects.MaxBrightness, TickFrequency = 10,
        SmallChange = 10, LargeChange = 10, Width = 300, BackColor = Ui.Back,
    };

    static Label Value() => new() { AutoSize = true, ForeColor = Ui.Dim, Font = Ui.Body, Margin = new Padding(8, 10, 0, 0) };

    static FlowLayoutPanel Pair(Control a, Control? b)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        f.Controls.Add(a);
        if (b is not null) f.Controls.Add(b);
        return f;
    }

    // ---------------------------------------------------------------- model <-> controls

    /// <summary>Schedules writing the current settings to the keyboard (edits in quick succession become one write).</summary>
    void Changed()
    {
        if (_loading) return;
        _edits++;
        _preview.Invalidate();
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    void Show(LightingMode mode, LayerConfig config)
    {
        _loading = true;
        _effect = LightingEffects.Find(config.Effect) is null ? Effect.Static : config.Effect;
        var info = LightingEffects.Find(_effect)!;
        FillChoices(info);
        _colorMode.SelectedItem = info.ColorModes.Contains(config.ColorMode) ? config.ColorMode : info.ColorModes[0];
        SetColors(info.ColorModes.Contains(config.ColorMode) ? config.Colors : info.DefaultColors[(ColorMode)_colorMode.SelectedItem!]);
        if (info.HasDirection) _direction.SelectedItem = info.Directions.Contains(config.Direction) ? config.Direction : info.Directions[0];
        _brightness.Value = Snap(config.Brightness);
        _speed.Value = Snap(config.Speed);
        _loading = false;
        HighlightEffect();
        _preview.Invalidate();
    }

    static int Snap(int v) => Math.Clamp((int)Math.Round(v / 10.0) * 10, LightingEffects.MinBrightness, LightingEffects.MaxBrightness);

    void FillChoices(EffectInfo info)
    {
        _colorMode.Items.Clear();
        foreach (var m in info.ColorModes) _colorMode.Items.Add(m);
        _direction.Items.Clear();
        foreach (var d in info.Directions) _direction.Items.Add(d);
        if (!info.HasDirection) { _direction.Items.Add("(not used by this effect)"); _direction.SelectedIndex = 0; }
        _directionRow.Enabled = info.HasDirection;
        _speedRow.Enabled = info.HasSpeed;
    }

    void SelectEffect(Effect effect)
    {
        var info = LightingEffects.Find(effect)!;
        Show(LightingMode.General, info.Default with { Brightness = _brightness.Value });
        Changed();
    }

    void HighlightEffect()
    {
        foreach (var (effect, b) in _effectButtons)
        {
            b.BackColor = effect == _effect ? Ui.Accent : Ui.Panel;
            b.ForeColor = effect == _effect ? Color.Black : Ui.Text;
        }
    }

    ColorMode CurrentColorMode => (ColorMode?)_colorMode.SelectedItem ?? ColorMode.Single;

    void ResetColors() => SetColors(LightingEffects.Find(_effect)!.DefaultColors[CurrentColorMode]);

    void SetColors(IReadOnlyList<GradientStop> stops)
    {
        _colors.Controls.Clear();
        foreach (var s in stops) Ui.Add(_colors, Swatch(s.Color));
        UpdateColorButtons();
        _preview.Invalidate();
    }

    ColorButton Swatch(Rgb c)
    {
        var b = new ColorButton { Value = Color.FromArgb(c.R, c.G, c.B) };
        b.ValueChanged += _ => Changed();
        return b;
    }

    void AddColor()
    {
        if (_colors.Controls.Count >= LightingEffects.MaxGradientStops) return;
        var last = ((ColorButton)_colors.Controls[^1]).Value;
        Ui.Add(_colors, Swatch(new Rgb(last.R, last.G, last.B)));
        UpdateColorButtons();
        Changed();
    }

    void RemoveColor()
    {
        if (_colors.Controls.Count <= LightingEffects.MinGradientStops) return;
        _colors.Controls.RemoveAt(_colors.Controls.Count - 1);
        UpdateColorButtons();
        Changed();
    }

    void UpdateColorButtons()
    {
        bool gradient = CurrentColorMode == ColorMode.Gradient;
        _addColor.Visible = _removeColor.Visible = gradient;
        _addColor.Enabled = _colors.Controls.Count < LightingEffects.MaxGradientStops;
        _removeColor.Enabled = _colors.Controls.Count > LightingEffects.MinGradientStops;
    }

    List<GradientStop> CurrentStops()
    {
        var swatches = _colors.Controls.OfType<ColorButton>().ToList();
        return swatches.Select((b, i) => new GradientStop(new Rgb(b.Value.R, b.Value.G, b.Value.B),
            swatches.Count == 1 ? 0 : (int)Math.Round(i * 100.0 / (swatches.Count - 1)))).ToList();
    }

    LayerConfig CurrentConfig()
    {
        var info = LightingEffects.Find(_effect)!;
        var direction = info.HasDirection ? (Direction)_direction.SelectedItem! : info.Default.Direction;
        return new LayerConfig(_effect, direction, _brightness.Value, info.HasSpeed ? _speed.Value : info.Default.Speed,
            CurrentColorMode, CurrentStops());
    }

    void PaintPreview(object? sender, PaintEventArgs e)
    {
        var stops = CurrentStops();
        var r = _preview.ClientRectangle;
        if (stops.Count == 0 || r.Width <= 1) return;
        if (stops.Count == 1)
        {
            using var b = new SolidBrush(ToColor(stops[0].Color));
            e.Graphics.FillRectangle(b, r);
            return;
        }
        using var brush = new LinearGradientBrush(r, Color.Black, Color.Black, 0f)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = stops.Select(s => ToColor(s.Color)).ToArray(),
                Positions = stops.Select((s, i) => i == 0 ? 0f : i == stops.Count - 1 ? 1f : s.Position / 100f).ToArray(),
            },
        };
        e.Graphics.FillRectangle(brush, r);
    }

    static Color ToColor(Rgb c) => Color.FromArgb(c.R, c.G, c.B);

    // ---------------------------------------------------------------- keyboard

    /// <summary>Re-reads the effect from the keyboard.</summary>
    public Task Reload() => LoadFromKeyboard();

    async Task LoadFromKeyboard()
    {
        _status.Text = "Reading the keyboard's lighting…";
        int edits = _edits;
        try
        {
            var (mode, config) = await _keyboard.Run(q =>
            {
                var l = new Lighting(q);
                return (l.GetMode(), l.GetLayerConfig());
            });
            if (IsDisposed) return; // the window closed while the keyboard answered
            if (edits != _edits || _applyTimer.Enabled) { _status.Text = ""; return; } // you changed something meanwhile
            // The keyboard may report its angled gradient variant; it's still a gradient here.
            if (config.ColorMode == ColorMode.OrientedGradient) config = config with { ColorMode = ColorMode.Gradient };
            Show(mode, config);
            _status.Text = mode is LightingMode.Custom or LightingMode.Realtime
                ? "The keyboard was left in desktop-driven lighting; any change here switches it back to this built-in effect."
                : mode == LightingMode.Off ? "The keyboard's lighting is off." : "Showing the keyboard's current effect.";
        }
        catch (Exception e) { if (!IsDisposed) _status.Text = Friendly(e); }
    }

    async Task Apply()
    {
        LayerConfig config;
        try { config = CurrentConfig(); }
        catch (ArgumentException e) { _status.Text = e.Message; return; }

        _beforeApply?.Invoke();
        _status.Text = "Saving to the keyboard…";
        try
        {
            await _keyboard.Run(q =>
            {
                KeyboardBackupGuard.EnsureBackup(q);
                var l = new Lighting(q);
                if (l.GetMode() != LightingMode.General) l.SetMode(LightingMode.General);
                l.SetLayerConfig(Lighting.TopLayer, config);
            });
            if (!IsDisposed) _status.Text = $"✔ {LightingEffects.Find(_effect)!.Name} saved to the keyboard.";
        }
        catch (Exception e) { if (!IsDisposed) _status.Text = Friendly(e); }
    }

    void RestoreOriginal()
    {
        if (KeyboardBackupGuard.Original is not { LayerConfig: { } config } original)
        {
            _status.Text = "No backup yet: your original lighting is saved automatically before the first change.";
            return;
        }
        Show(original.LightingMode ?? LightingMode.General, config);
        Changed();
        _status.Text = "Restoring your original lighting…";
    }

    static string Friendly(Exception e) => e is KeyboardUnavailableException ? e.Message : $"Something went wrong: {e.Message}";
}
