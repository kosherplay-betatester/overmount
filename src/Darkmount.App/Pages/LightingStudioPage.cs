using System.Diagnostics;
using System.Text.Json;
using Darkmount.App.Overlays;
using Darkmount.Keyboard;
using Darkmount.Keyboard.Lamps;

namespace Darkmount.App.Pages;

/// <summary>
/// IO Center-style custom lighting, run by OverMount: layers of effects, each on its own selection of keys and
/// edge LEDs, per-key painting, a gallery of premade scenes, live preview, and the live overlays. Every change applies
/// immediately and makes the studio the active lighting.
/// </summary>
public sealed class LightingStudioPage : Ui.Page
{
    const int MaxLayers = LightingScene.MaxLayers, EdgeIdOffset = 1000, SyntheticKeyLamp = 10000;

    static readonly string[] Swatches =
    [
        "FF0000", "FF2800", "FF7800", "FFC800", "FFFF00", "7CFF00", "00FF3C", "00FFC8", "00C8FF", "0050FF", "5A00FF", "B400FF",
        "FF00C8", "FF4D88", "FFFFFF", "000000",
    ];

    readonly Func<AppSettings> _get;
    readonly Action<AppSettings> _apply;
    readonly Func<IReadOnlyList<LampPoint>> _deviceLayout;
    readonly Func<IReadOnlyDictionary<int, LampColor>> _liveFrame;
    readonly Action? _activated;
    readonly LightingScene _scene;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 90 };

    readonly ListBox _layers = new() { Width = 260, Height = 210, Font = Ui.Body, BackColor = Ui.Panel, ForeColor = Ui.Text, BorderStyle = BorderStyle.None };
    readonly KeyboardView _view = new() { Size = new Size(920, 340), MultiSelect = true, Margin = new Padding(0, 4, 0, 4) };
    readonly FlowLayoutPanel _effects = new() { AutoSize = true, WrapContents = true, MaximumSize = new Size(900, 0) };
    readonly ComboBox _colorMode = Ui.Combo<SceneColorMode>(160), _direction = Ui.Combo<SceneDirection>(170);
    readonly ComboBox _numpad = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, Font = Ui.Body };
    readonly FlowLayoutPanel _colors = new() { AutoSize = true, WrapContents = false };
    readonly FlowLayoutPanel _palette = new() { AutoSize = true, WrapContents = true, MaximumSize = new Size(900, 0) };
    readonly Control _colorsRow, _directionRow, _speedRow, _paintRow;
    readonly TrackBar _speed = new() { Minimum = 1, Maximum = 10, Width = 240, BackColor = Ui.Back },
        _brightness = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Width = 240, BackColor = Ui.Back };
    readonly Label _effectInfo = Ui.Note("", 880);
    readonly Label _selectHint = Ui.Note("", 880);
    readonly Label _status = Ui.Note("", 880);
    readonly Dictionary<SceneEffect, Button> _effectButtons = [];
    readonly List<Control> _brushButtons = [];

    // Overlays and behaviour
    readonly CheckBox _lock = Ui.Check("Lock keys glow (Caps, Num, Scroll)"), _vol = Ui.Check("Volume bar on F1–F12 when the volume changes"),
        _mic = Ui.Check("Microphone muted: key glows red"), _shortcut = Ui.Check("Shortcut helper while holding Ctrl / Alt / Win"),
        _timer = Ui.Check("Focus-timer progress on F1–F12"), _alert = Ui.Check("Flash red while a dock alert is showing"),
        _dim = Ui.Check("Lights off while Windows is locked or idle");
    readonly NumericUpDown _idle = Ui.Number(0, 240);

    IReadOnlyList<LampPoint> _previewLayout = [];
    string? _brush = "FF2800"; // null = eraser
    bool _loading;

    /// <param name="activated">Called after an edit made the studio the active lighting (the host updates its switch).</param>
    public LightingStudioPage(Func<AppSettings> get, Action<AppSettings> apply,
        Func<IReadOnlyList<LampPoint>> deviceLayout, Func<IReadOnlyDictionary<int, LampColor>> liveFrame, Action? activated = null)
        : base("Lighting studio", "Build your own lighting from layers: each layer is an effect on the keys and edge LEDs you " +
                                  "select (click, Ctrl+click or drag a box). The top layer wins. Paint layers give every key and " +
                                  "edge LED its own colour. Or start from a preset.")
    {
        _get = get;
        _apply = apply;
        _deviceLayout = deviceLayout;
        _liveFrame = liveFrame;
        _activated = activated;
        var s = get();
        _scene = (s.Scene ?? ScenePresets.All[0]).Clone();

        // Presets gallery
        var presets = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(920, 0) };
        foreach (var preset in ScenePresets.All)
        {
            var b = Ui.Button(preset.Name, (_, _) => LoadPreset(preset));
            b.MinimumSize = new Size(124, 34);
            new ToolTip().SetToolTip(b, preset.Description);
            presets.Controls.Add(b);
        }
        Heading("Presets");
        AddFull(presets);

        Heading("Your scene");
        var layerButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        layerButtons.Controls.AddRange([
            Ui.Button("+ Effect layer", (_, _) => AddLayer(paint: false)), Ui.Button("+ Paint layer (per-key colours)", (_, _) => AddLayer(paint: true)),
            Ui.Button("Delete layer", (_, _) => DeleteLayer()), Ui.Button("Move up", (_, _) => MoveLayer(-1)),
            Ui.Button("Move down", (_, _) => MoveLayer(1)), Ui.Button("On / off", (_, _) => ToggleLayer())]);
        var numpadRow = Pair(Hint("Numpad on the"), _numpad);
        layerButtons.Controls.Add(numpadRow);
        var layerRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        layerRow.Controls.AddRange([_layers, layerButtons]);
        AddFull(layerRow);
        AddFull(_selectHint);
        AddFull(_view);

        var groups = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(920, 0) };
        groups.Controls.Add(Hint("Quick select:"));
        foreach (var (name, _) in Groups())
        {
            var b = Ui.Button(name, (_, _) => SelectGroup(name));
            b.MinimumSize = new Size(0, 30);
            groups.Controls.Add(b);
        }
        AddFull(groups);

        // Paint tools (paint layers only)
        foreach (var hex in Swatches)
        {
            var sw = new Button
            {
                Size = new Size(34, 34), FlatStyle = FlatStyle.Flat, BackColor = ToColor(hex), Margin = new Padding(0, 0, 6, 6),
                Cursor = Cursors.Hand, Tag = hex,
            };
            sw.FlatAppearance.BorderColor = Ui.Panel;
            sw.Click += (_, _) => SetBrush(hex);
            _brushButtons.Add(sw);
            _palette.Controls.Add(sw);
        }
        var custom = new ColorButton { Value = ToColor("FF2800"), Margin = new Padding(6, 0, 6, 6) };
        custom.ValueChanged += c => SetBrush(Hex(c.Value));
        var eraser = Ui.Button("Eraser", (_, _) => SetBrush(null));
        eraser.Tag = "eraser";
        _brushButtons.Add(eraser);
        _palette.Controls.AddRange([Hint("custom:"), custom, eraser, Ui.Button("Fill everything", (_, _) => FillAll()),
            Ui.Button("Clear all", (_, _) => ClearPaint())]);
        _paintRow = _palette;
        Row("Paint colour", _paintRow);

        foreach (var info in SceneEffects.All.Where(i => i.Effect != SceneEffect.PerKey))
        {
            var b = Ui.Button(info.Name, (_, _) => SetEffect(info.Effect));
            b.MinimumSize = new Size(112, 32);
            new ToolTip().SetToolTip(b, info.Description);
            _effectButtons[info.Effect] = b;
            _effects.Controls.Add(b);
        }
        Row("Effect", _effects);
        AddFull(_effectInfo);
        _colorsRow = Pair(_colorMode, _colors, Ui.Button("+", (_, _) => AddColor()), Ui.Button("−", (_, _) => RemoveColor()));
        Row("Colours", _colorsRow);
        _directionRow = _direction;
        Row("Direction", _direction);
        _speedRow = _speed;
        Row("Speed", _speed);
        Row("Brightness", _brightness);

        Heading("Live extras (drawn on top of your scene)");
        var overlays = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        overlays.Controls.AddRange([_lock, _vol, _mic, _shortcut, _timer, _alert, Pair(_dim, Hint("after"), _idle, Hint("idle minutes (0 = only when locked)"))]);
        AddFull(overlays);
        AddFull(_status);

        // Wire edits.
        _numpad.Items.AddRange([NumpadSide.Left, NumpadSide.Right]);
        _numpad.SelectedItem = s.NumpadSide == NumpadSide.Left ? NumpadSide.Left : NumpadSide.Right;
        _numpad.SelectedIndexChanged += (_, _) => { if (!_loading) { BuildView(); ShowLayer(); Commit(activate: false); } };
        _layers.SelectedIndexChanged += (_, _) => ShowLayer();
        _layers.DoubleClick += (_, _) => RenameLayer();
        _view.SelectionChanged += OnSelection;
        _colorMode.SelectedIndexChanged += (_, _) => { if (!_loading && Current is { } l) { l.ColorMode = (SceneColorMode)_colorMode.SelectedItem!; FixColors(l); ShowColors(); Commit(); } };
        _direction.SelectedIndexChanged += (_, _) => { if (!_loading && Current is { } l) { l.Direction = (SceneDirection)_direction.SelectedItem!; Commit(); } };
        _speed.ValueChanged += (_, _) => { if (!_loading && Current is { } l) { l.Speed = _speed.Value; Commit(); } };
        _brightness.ValueChanged += (_, _) => { if (!_loading && Current is { } l) { l.Brightness = _brightness.Value; Commit(); } };
        foreach (var c in new[] { _lock, _vol, _mic, _shortcut, _timer, _alert, _dim }) c.CheckedChanged += (_, _) => Commit(activate: false);
        _idle.ValueChanged += (_, _) => Commit(activate: false);

        _loading = true;
        (_lock.Checked, _vol.Checked, _mic.Checked, _shortcut.Checked, _timer.Checked) =
            (s.Overlays.LockKeys, s.Overlays.VolumeBar, s.Overlays.MicMute, s.Overlays.ShortcutHelper, s.Overlays.FocusTimerBar);
        (_alert.Checked, _dim.Checked, _idle.Value) = (s.RgbAlertFlash, s.RgbDimWhenLocked, Math.Clamp(s.RgbIdleMinutes, 0, 240));
        _loading = false;

        SetBrush(_brush);
        BuildView();
        RefreshLayers();
        // The studio can sit inside another page, so it watches its own visibility instead of relying on VisibleChanged.
        _previewTimer.Tick += (_, _) => Preview();
        _previewTimer.Start();
        Disposed += (_, _) => _previewTimer.Dispose();
    }

    LightLayer? Current => _layers.SelectedIndex >= 0 && _layers.SelectedIndex < _scene.Layers.Count ? _scene.Layers[_layers.SelectedIndex] : null;

    bool Painting => Current?.Effect == SceneEffect.PerKey;

    NumpadSide Side => _numpad.SelectedItem is NumpadSide.Left ? NumpadSide.Left : NumpadSide.Right;

    static Label Hint(string t) => new() { Text = t, AutoSize = true, ForeColor = Ui.Dim, Margin = new Padding(6, 9, 6, 0) };

    static FlowLayoutPanel Pair(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        f.Controls.AddRange(controls);
        return f;
    }

    // ---------------------------------------------------------------- keyboard picture

    /// <summary>
    /// Keys from the keyboard geometry; edge LEDs as IO Center shows them: a ring around the main block (64) and one around
    /// the numpad (32), placed by edge-light number.
    /// </summary>
    void BuildView()
    {
        // Keys with LEDs only (the display keys have screens, not RGB).
        var rects = KeyGeometry.Keys(PhysicalLayout.Ansi, Side).Where(r => KeyIds.Find(r.KeyId)?.Zone is KeyZone.Keyboard or KeyZone.Numpad).ToList();
        _previewLayout = _deviceLayout() is { Count: > 0 } real ? real : SyntheticLayout(rects);
        var shapes = rects.Select(r => new KeyShape(r.KeyId, new RectangleF(r.X, r.Y, r.Width, r.Height), KeysPage.ShortLabel(r.KeyId))).ToList();
        var (main, pad) = Sections(rects);
        float h = rects.Max(r => r.Y + r.Height), led = h * 0.012f;
        foreach (var p in _previewLayout.Where(p => !p.IsKey))
        {
            PointF c;
            bool horizontal = true;
            if (p.Edge > 0)
            {
                c = RingPoint(p.Edge, main, pad);
                horizontal = Math.Abs(c.Y - main.Top) < 1 || Math.Abs(c.Y - main.Bottom) < 1 || Math.Abs(c.Y - pad.Top) < 1 || Math.Abs(c.Y - pad.Bottom) < 1;
            }
            else
            {
                var all = RectangleF.Union(main, pad);
                double dx = p.X - 0.5, dy = p.Y - 0.5, k = 0.5 / Math.Max(Math.Abs(dx), Math.Abs(dy) * 1.0001 + 1e-9);
                c = new PointF((float)(all.Left + (0.5 + dx * k) * all.Width), (float)(all.Top + (0.5 + dy * k) * all.Height));
            }
            var size = horizontal ? new SizeF(led * 3.2f, led * 1.3f) : new SizeF(led * 1.3f, led * 3.2f);
            shapes.Add(new KeyShape(EdgeIdOffset + p.LampId, new RectangleF(c.X - size.Width / 2, c.Y - size.Height / 2, size.Width, size.Height), "", Led: true));
        }
        _view.KeyShapes = shapes;
    }

    /// <summary>The main block's and the numpad's bounding boxes, pushed out a little for their LED rings.</summary>
    static (RectangleF Main, RectangleF Pad) Sections(IReadOnlyList<KeyRect> rects)
    {
        static RectangleF Box(IEnumerable<KeyRect> keys) =>
            keys.Select(r => new RectangleF(r.X, r.Y, r.Width, r.Height)).DefaultIfEmpty(RectangleF.Empty).Aggregate(RectangleF.Union);
        var main = Box(rects.Where(r => KeyIds.Find(r.KeyId)?.Zone == KeyZone.Keyboard));
        var pad = Box(rects.Where(r => KeyIds.Find(r.KeyId)?.Zone == KeyZone.Numpad));
        float m = main.Height * 0.06f;
        main.Inflate(m, m);
        pad.Inflate(m, m);
        return (main, pad);
    }

    /// <summary>
    /// Where edge light <paramref name="edge"/> sits: each ring runs clockwise from its top-left corner — top (21 / 5 LEDs),
    /// right side incl. both corners (11), bottom right→left (21 / 5), left side up to the ring start.
    /// </summary>
    static PointF RingPoint(int edge, RectangleF main, RectangleF pad)
    {
        bool numpad = edge > DarkmountKeys.KeyboardEdgeLights;
        var r = numpad ? pad : main;
        int across = numpad ? 5 : 21, i = edge - (numpad ? DarkmountKeys.KeyboardEdgeLights + 1 : 1);
        if (i <= across) return new(r.Left + r.Width * i / (across + 1), r.Top);
        i -= across + 1;
        if (i <= 10) return new(r.Right, r.Top + r.Height * i / 10);
        i -= 10;
        if (i <= across) return new(r.Right - r.Width * i / (across + 1), r.Bottom);
        i -= across + 1;
        return new(r.Left, r.Bottom - r.Height * i / 10);
    }

    /// <summary>A stand-in layout (Dark Mount lamp ids for the edge LEDs) while the keyboard's lighting interface isn't open.</summary>
    static IReadOnlyList<LampPoint> SyntheticLayout(IReadOnlyList<KeyRect> rects)
    {
        var (main, pad) = Sections(rects);
        var all = RectangleF.Union(main, pad);
        var list = rects.Select(r => new LampPoint(SyntheticKeyLamp + r.KeyId, (r.X + r.Width / 2.0 - all.Left) / all.Width,
            (r.Y + r.Height / 2.0 - all.Top) / all.Height, true, r.KeyId)).ToList();
        foreach (var (edge, lamp) in DarkmountKeys.DefaultEdgeLights)
        {
            var c = RingPoint(edge, main, pad);
            list.Add(new LampPoint(lamp, (c.X - all.Left) / all.Width, (c.Y - all.Top) / all.Height, false, 0, edge));
        }
        return list;
    }


    bool _shown;

    /// <summary>Just became visible: pick up changes made elsewhere (profiles, imports) and the real lamp layout.</summary>
    void OnShown()
    {
        if (JsonSerializer.Serialize(_get().Scene) is var saved && saved != "null" && saved != JsonSerializer.Serialize(_scene)) Reload();
        BuildView();
        ShowLayer();
    }

    /// <summary>
    /// The keyboard picture fills the width of the settings page (the studio sits inside the Lighting page, so it finds
    /// the scrolling page host rather than relying on its own parent's resize events).
    /// </summary>
    void FitKeyboard()
    {
        Control? host = Parent;
        while (host is not null && host is not ScrollableControl { AutoScroll: true }) host = host.Parent;
        if (host is null || _view.Parent is null) return;
        int left = host.PointToClient(_view.Parent.PointToScreen(_view.Location)).X;
        int width = Math.Max(LogicalToDeviceUnits(900), host.ClientSize.Width - left - LogicalToDeviceUnits(28)); // 96-DPI units
        if (Math.Abs(width - _view.Width) > 4) _view.Size = new Size(width, (int)(width * 0.37));
    }

    void Preview()
    {
        if (!Visible) { _shown = false; return; }
        FitKeyboard();
        if (!_shown) { _shown = true; OnShown(); }
        else if (_deviceLayout().Count > 0 && _previewLayout.Any(p => p.LampId >= SyntheticKeyLamp)) BuildView(); // keyboard appeared
        var live = _deviceLayout().Count > 0 && _get().RgbEnabled ? _liveFrame() : null;
        var frame = live is { Count: > 0 } ? live : SceneRenderer.Render(_scene, new SceneContext
        {
            Seconds = _clock.Elapsed.TotalSeconds, Lamps = _previewLayout,
            CpuTemp = 55, CpuLoad = 35, GpuTemp = 50, GpuLoad = 40, AudioLevel = 0.5 + 0.4 * Math.Sin(_clock.Elapsed.TotalSeconds * 3),
        });
        var colors = new Dictionary<int, Color>();
        foreach (var p in _previewLayout)
            if (frame.TryGetValue(p.LampId, out var c))
                colors[p.IsKey ? p.KeyId : EdgeIdOffset + p.LampId] = Color.FromArgb(c.R, c.G, c.B);
        _view.SetColors(colors);
    }

    // ---------------------------------------------------------------- selections

    IEnumerable<int> EdgeIds(Func<int, bool> edge) =>
        _previewLayout.Where(p => !p.IsKey && p.Edge > 0 && edge(p.Edge)).Select(p => EdgeIdOffset + p.LampId);

    static IEnumerable<int> KeysWhere(Func<KeyInfo, bool> match) =>
        KeyIds.All.Where(k => k.Zone is KeyZone.Keyboard or KeyZone.Numpad && !k.IsoOnly && match(k)).Select(k => (int)k.Id);

    static bool Usage(KeyInfo k, int from, int to) => k.HidUsage is { } u && u >= from && u <= to;

    IEnumerable<(string Name, Func<IEnumerable<int>> Ids)> Groups() =>
    [
        ("WASD", () => KeysWhere(k => k.HidUsage is 0x1A or 0x04 or 0x16 or 0x07)),
        ("Arrows", () => KeysWhere(k => Usage(k, 0x4F, 0x52))),
        ("F1–F12", () => KeysWhere(k => Usage(k, 0x3A, 0x45))),
        ("Numbers", () => KeysWhere(k => Usage(k, 0x1E, 0x27))),
        ("Letters", () => KeysWhere(k => Usage(k, 0x04, 0x1D))),
        ("Numpad", () => KeysWhere(k => k.Zone == KeyZone.Numpad)),
        ("Modifiers", () => KeysWhere(k => Usage(k, 0xE0, 0xE7) || k.Id == 55)),
        ("All keys", () => KeysWhere(_ => true)),
        ("Top edge", () => EdgeIds(e => e is >= 1 and <= 22 || e is >= 65 and <= 70)),
        ("Right edge", () => EdgeIds(e => Side == NumpadSide.Right ? e is >= 71 and <= 81 : e is >= 23 and <= 33)),
        ("Bottom edge", () => EdgeIds(e => e is >= 34 and <= 54 || e is >= 82 and <= 86)),
        ("Left edge", () => EdgeIds(e => Side == NumpadSide.Left ? e is >= 87 and <= 96 or 65 : e is >= 55 and <= 64 or 1)),
        ("Keyboard ring", () => EdgeIds(e => e <= DarkmountKeys.KeyboardEdgeLights)),
        ("Numpad ring", () => EdgeIds(e => e > DarkmountKeys.KeyboardEdgeLights)),
        ("All edge LEDs", () => _previewLayout.Where(p => !p.IsKey).Select(p => EdgeIdOffset + p.LampId)),
        ("Invert", () => AllIds().Except(Painting ? PaintedIds() : _view.SelectedKeys)),
        ("Nothing", () => []),
    ];

    IEnumerable<int> AllIds() => KeysWhere(_ => true).Concat(_previewLayout.Where(p => !p.IsKey).Select(p => EdgeIdOffset + p.LampId));

    IEnumerable<int> PaintedIds() => Current is { } l
        ? l.KeyColors.Keys.Concat(l.EdgeColors.Keys.Select(e => EdgeIdOffset + e)) : [];

    /// <summary>Selects a group (Ctrl adds to the selection); on a paint layer the group is painted right away.</summary>
    void SelectGroup(string name)
    {
        if (Current is null) { _status.Text = "Add a layer first."; return; }
        var ids = Groups().First(g => g.Name == name).Ids().ToList();
        if (!Painting && ModifierKeys.HasFlag(Keys.Control) && name != "Nothing") ids = ids.Union(_view.SelectedKeys).ToList();
        _view.Select(ids);
    }

    void OnSelection()
    {
        if (_loading || Current is not { } l) return;
        var selected = _view.SelectedKeys.ToList();
        if (l.Effect == SceneEffect.PerKey)
        {
            if (selected.Count == 0) return;
            Paint(l, selected);
            _view.SelectionChanged -= OnSelection;
            _view.Select([]);
            _view.SelectionChanged += OnSelection;
            Commit();
            return;
        }
        var keys = selected.Where(i => i < EdgeIdOffset).ToList();
        var edges = selected.Where(i => i >= EdgeIdOffset).Select(i => i - EdgeIdOffset).ToList();
        int allEdges = _previewLayout.Count(p => !p.IsKey);
        l.AllKeys = keys.Count >= KeysWhere(_ => true).Count();
        l.Keys = l.AllKeys ? [] : keys;
        l.AllEdges = allEdges > 0 && edges.Count >= allEdges;
        l.EdgeLamps = l.AllEdges ? [] : edges;
        Commit();
    }

    // ---------------------------------------------------------------- painting

    void SetBrush(string? hex)
    {
        _brush = hex;
        foreach (var b in _brushButtons)
        {
            bool on = b.Tag as string == (hex ?? "eraser");
            if (b is Button button) button.FlatAppearance.BorderColor = on ? Color.White : Ui.Panel;
            if (b is Button { Tag: "eraser" } e) (e.BackColor, e.ForeColor) = on ? (Ui.Accent, Color.Black) : (Ui.Panel, Ui.Text);
            b.Padding = on ? new Padding(2) : Padding.Empty;
        }
        if (Painting) _selectHint.Text = PaintHint();
    }

    string PaintHint() => _brush is null
        ? "Eraser: click or drag over keys and edge LEDs to clear them (lower layers show through)."
        : "Click or drag over keys and edge LEDs to paint them. Pick another colour any time; Quick select paints whole groups.";

    void Paint(LightLayer l, IEnumerable<int> ids)
    {
        foreach (var id in ids)
        {
            var target = id < EdgeIdOffset ? l.KeyColors : l.EdgeColors;
            int key = id < EdgeIdOffset ? id : id - EdgeIdOffset;
            if (_brush is null) target.Remove(key);
            else target[key] = _brush;
        }
    }

    void FillAll()
    {
        if (Current is not { Effect: SceneEffect.PerKey } l) return;
        Paint(l, AllIds());
        Commit();
    }

    void ClearPaint()
    {
        if (Current is not { Effect: SceneEffect.PerKey } l) return;
        l.KeyColors.Clear();
        l.EdgeColors.Clear();
        Commit();
    }

    // ---------------------------------------------------------------- layers

    void RefreshLayers()
    {
        int sel = Math.Max(0, _layers.SelectedIndex);
        _layers.Items.Clear();
        foreach (var l in _scene.Layers) _layers.Items.Add($"{(l.Enabled ? "●" : "○")} {l.Name} — {SceneEffects.Get(l.Effect).Name}");
        if (_layers.Items.Count > 0) _layers.SelectedIndex = Math.Min(sel, _layers.Items.Count - 1);
        ShowLayer();
    }

    void ShowLayer()
    {
        var l = Current;
        _loading = true;
        bool paint = l?.Effect == SceneEffect.PerKey;
        foreach (var (effect, b) in _effectButtons)
        {
            bool on = l?.Effect == effect;
            b.BackColor = on ? Ui.Accent : Ui.Panel;
            b.ForeColor = on ? Color.Black : Ui.Text;
        }
        SetRowVisible(_paintRow, paint);
        SetRowVisible(_effects, !paint);
        SetRowVisible(_colorsRow, !paint);
        SetRowVisible(_directionRow, !paint);
        SetRowVisible(_speedRow, !paint);
        _effectInfo.Visible = !paint;
        _view.SelectionChanged -= OnSelection;
        if (l is not null)
        {
            var info = SceneEffects.Get(l.Effect);
            _colorMode.Items.Clear();
            foreach (var m in info.ColorModes) _colorMode.Items.Add(m);
            _colorMode.SelectedItem = info.ColorModes.Contains(l.ColorMode) ? l.ColorMode : info.ColorModes.FirstOrDefault();
            _colorMode.Enabled = info.ColorModes.Count > 0;
            _direction.Items.Clear();
            foreach (var d in info.Directions) _direction.Items.Add(d);
            if (info.Directions.Count > 0) _direction.SelectedItem = info.Directions.Contains(l.Direction) ? l.Direction : info.Directions[0];
            _direction.Enabled = info.Directions.Count > 0;
            _speed.Value = Math.Clamp(l.Speed, 1, 10);
            _speed.Enabled = info.HasSpeed;
            _brightness.Value = Math.Clamp(l.Brightness, 0, 100);
            _effectInfo.Text = info.Description + (info.NeedsKeyPresses ? " Reacts to your typing." : "") +
                (info.NeedsAudio ? " Listens to what your PC plays." : "") + (info.NeedsScreen ? " Follows your screen." : "") +
                (info.NeedsSensors ? " Uses CPU/GPU sensors." : "");
            if (paint)
            {
                _view.Select([]);
                _selectHint.Text = PaintHint();
            }
            else
            {
                var sel = (l.AllKeys ? KeysWhere(_ => true) : l.Keys)
                    .Concat(l.AllEdges ? _previewLayout.Where(p => !p.IsKey).Select(p => EdgeIdOffset + p.LampId) : l.EdgeLamps.Select(e => EdgeIdOffset + e));
                _view.Select(sel);
                _selectHint.Text = "Highlighted keys and LEDs belong to this layer. Click, Ctrl+click or drag a box to change them.";
            }
        }
        else _selectHint.Text = "Pick a preset above, or add a layer to start from scratch.";
        _view.SelectionChanged += OnSelection;
        ShowColors();
        _loading = false;
    }

    /// <summary>Shows or hides a <see cref="Ui.Page.Row"/> (its label is the control just before it in the table).</summary>
    void SetRowVisible(Control c, bool visible)
    {
        var cell = c.Parent == this ? c : c.Parent is { } p && p.Parent == this ? p : c;
        cell.Visible = visible;
        int i = Controls.GetChildIndex(cell);
        if (i > 0 && Controls[i - 1] is Label label) label.Visible = visible;
    }

    void ShowColors()
    {
        _colors.Controls.Clear();
        if (Current is not { } l || SceneEffects.Get(l.Effect).ColorModes.Count == 0) return;
        foreach (var hex in l.Colors)
        {
            var b = new ColorButton { Value = ToColor(hex) };
            b.ValueChanged += _ => { l.Colors = _colors.Controls.OfType<ColorButton>().Select(x => Hex(x.Value)).ToList(); Commit(); };
            Ui.Add(_colors, b);
        }
    }

    static void FixColors(LightLayer l)
    {
        int want = l.ColorMode switch { SceneColorMode.Single => 1, SceneColorMode.Dual => 2, _ => Math.Clamp(l.Colors.Count, 2, 7) };
        while (l.Colors.Count < want) l.Colors.Add(l.Colors.Count == 0 ? "FF2800" : "FFFFFF");
        if (l.Colors.Count > want) l.Colors = l.Colors.Take(want).ToList();
    }

    void AddColor()
    {
        if (Current is not { } l || l.Colors.Count >= 7 || !SceneEffects.Get(l.Effect).ColorModes.Contains(SceneColorMode.Gradient)) return;
        l.ColorMode = SceneColorMode.Gradient;
        l.Colors.Add(l.Colors.LastOrDefault() ?? "FFFFFF");
        FixColors(l);
        ShowLayer();
        Commit();
    }

    void RemoveColor()
    {
        if (Current is not { } l || l.Colors.Count <= 1) return;
        l.Colors.RemoveAt(l.Colors.Count - 1);
        l.ColorMode = l.Colors.Count switch { 1 => SceneColorMode.Single, 2 when l.ColorMode == SceneColorMode.Gradient => SceneColorMode.Gradient, _ => l.ColorMode };
        FixColors(l);
        ShowLayer();
        Commit();
    }

    void SetEffect(SceneEffect effect)
    {
        if (Current is null || Painting) { AddLayer(paint: false); if (Current is null) return; }
        var layer = Current!;
        layer.Effect = effect;
        var info = SceneEffects.Get(effect);
        if (!info.ColorModes.Contains(layer.ColorMode) && info.ColorModes.Count > 0) layer.ColorMode = info.ColorModes[0];
        if (info.Directions.Count > 0 && !info.Directions.Contains(layer.Direction)) layer.Direction = info.Directions[0];
        FixColors(layer);
        if (layer.Name.StartsWith("Layer", StringComparison.Ordinal) || SceneEffects.All.Any(i => i.Name == layer.Name)) layer.Name = info.Name;
        RefreshLayers();
        Commit();
    }

    void AddLayer(bool paint)
    {
        if (_scene.Layers.Count >= MaxLayers) { _status.Text = $"A scene can have up to {MaxLayers} layers."; return; }
        var layer = paint
            ? new LightLayer { Name = "Painted keys", Effect = SceneEffect.PerKey, AllKeys = true, AllEdges = true }
            : new LightLayer { Name = $"Layer {_scene.Layers.Count + 1}", AllKeys = false, AllEdges = false };
        _scene.Layers.Insert(0, layer);
        _layers.SelectedIndex = -1;
        RefreshLayers();
        _layers.SelectedIndex = 0;
        _status.Text = paint
            ? "Paint layer on top: pick a colour, then click or drag over keys and edge LEDs."
            : "New layer on top. Select the keys it should light, then pick an effect.";
        Commit();
    }

    void DeleteLayer()
    {
        if (Current is null) return;
        _scene.Layers.RemoveAt(_layers.SelectedIndex);
        RefreshLayers();
        Commit();
    }

    void MoveLayer(int delta)
    {
        int i = _layers.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= _scene.Layers.Count) return;
        (_scene.Layers[i], _scene.Layers[j]) = (_scene.Layers[j], _scene.Layers[i]);
        _layers.SelectedIndex = -1;
        RefreshLayers();
        _layers.SelectedIndex = j;
        Commit();
    }

    void ToggleLayer()
    {
        if (Current is not { } l) return;
        l.Enabled = !l.Enabled;
        RefreshLayers();
        Commit();
    }

    void RenameLayer()
    {
        if (Current is not { } l) return;
        if (Ui.Prompt(FindForm(), "Rename layer", "Layer name", l.Name) is not { } name || string.IsNullOrWhiteSpace(name)) return;
        l.Name = name.Trim();
        RefreshLayers();
        Commit();
    }

    void LoadPreset(LightingScene preset)
    {
        var copy = preset.Clone();
        _scene.Name = copy.Name;
        _scene.Description = copy.Description;
        _scene.Background = copy.Background;
        _scene.Layers = copy.Layers;
        _layers.SelectedIndex = -1;
        RefreshLayers();
        _status.Text = $"\"{copy.Name}\" — {copy.Description}";
        Commit();
    }

    /// <summary>Reloads the scene from the settings (after a profile or an import changed it).</summary>
    public void Reload()
    {
        var fresh = (_get().Scene ?? ScenePresets.All[0]).Clone();
        _scene.Name = fresh.Name;
        _scene.Description = fresh.Description;
        _scene.Background = fresh.Background;
        _scene.Layers = fresh.Layers;
        RefreshLayers();
    }

    /// <summary>Applies the edited scene and options immediately (and saves them). Scene edits make the studio active.</summary>
    void Commit(bool activate = true)
    {
        if (_loading) return;
        var s = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_get()))!;
        bool wasActive = s.RgbEnabled;
        s.Scene = _scene.Clone();
        if (activate) s.RgbEnabled = true;
        s.NumpadSide = Side;
        s.RgbAlertFlash = _alert.Checked;
        s.RgbDimWhenLocked = _dim.Checked;
        s.RgbIdleMinutes = (int)_idle.Value;
        s.Overlays = new OverlaySettings
        {
            LockKeys = _lock.Checked, VolumeBar = _vol.Checked, MicMute = _mic.Checked, MicMuteKey = s.Overlays.MicMuteKey,
            ShortcutHelper = _shortcut.Checked, FocusTimerBar = _timer.Checked,
        };
        _apply(s);
        if (activate && !wasActive) _activated?.Invoke();
    }

    static Color ToColor(string hex)
    {
        try { var c = Rgb.Parse(hex); return Color.FromArgb(c.R, c.G, c.B); }
        catch (Exception e) when (e is FormatException or ArgumentException) { return Color.OrangeRed; }
    }

    static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";
}
