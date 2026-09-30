using System.Diagnostics;
using System.Text.Json;
using Darkmount.App.Overlays;
using Darkmount.Keyboard;
using Darkmount.Keyboard.Lamps;

namespace Darkmount.App.Pages;

/// <summary>
/// IO Center-style custom lighting, run by OverMount: layers of effects, each on its own selection of keys and
/// edge LEDs, per-key painting, a searchable gallery of premade scenes with favourites, the user's own saved presets
/// (import/export as files), undo, a colour remix, live preview, and the live overlays. Every change applies
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
    readonly ChipPicker _effects = new(wrapWidth: 900);
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
    readonly List<Control> _brushButtons = [];

    // Overlays and behaviour
    readonly CheckBox _lock = Ui.Check("Lock keys glow (Caps, Num, Scroll)"), _vol = Ui.Check("Volume bar on F1–F12 when the volume changes"),
        _mic = Ui.Check("Microphone muted: key glows red"), _shortcut = Ui.Check("Shortcut helper while holding Ctrl / Alt / Win"),
        _timer = Ui.Check("Focus-timer progress on F1–F12"), _alert = Ui.Check("Flash red while a dock alert is showing"),
        _dim = Ui.Check("Lights off while Windows is locked or idle");
    readonly NumericUpDown _idle = Ui.Number(0, 240);

    // Presets library: search, My presets, Favourites and the built-in categories.
    readonly TextBox _search = new()
    {
        Width = 300, Font = Ui.Body, PlaceholderText = "Search presets and effects", BackColor = Ui.Panel, ForeColor = Ui.Text,
        BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 6, 10, 4),
    };
    readonly Label _myHeading = CategoryLabel("MY PRESETS"), _favHeading = CategoryLabel("FAVOURITES");
    readonly Label _myEmpty = Ui.Note("Nothing saved yet. Build a scene below (or tweak any preset) and click \"Save as my preset\".", 880);
    readonly Label _noMatch = Ui.Note("No preset matches your search.", 880);
    readonly Label _presetStatus = Ui.Note("Tip: right-click a preset to add it to Favourites, save a copy to My presets or export it.", 880);
    readonly Label _sceneStatus = Ui.Note("", 880);
    readonly ChipPicker _myRow = PresetRow(), _favRow = PresetRow();
    readonly List<(Label Heading, ChipPicker Row, IReadOnlyList<LightingScene> Scenes)> _categories = [];
    readonly Dictionary<string, LightingScene> _builtIn;
    readonly ToolTip _tips = new();
    readonly Random _random = new();

    // Undo: snapshots of the scene (JSON) before each change; quick bursts (dragging a slider) count as one step.
    readonly Button _undoButton, _redoButton;
    readonly List<string> _undo = [], _redo = [];
    string _lastScene;
    long _lastUndoPush;
    bool _restoring;

    /// <summary>The preset the scene was loaded from (highlighted in the list).</summary>
    PresetRef? _loaded;

    sealed record PresetRef(string Name, bool Mine);

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

        _lastScene = JsonSerializer.Serialize(_scene);
        _builtIn = ScenePresets.All.DistinctBy(p => p.Name).ToDictionary(p => p.Name);
        _loaded = SourceOf(_scene.Name);

        // Presets gallery: My presets, Favourites, then by category ("Nature", "Gaming", …); tooltips describe each one.
        var presets = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        presets.Controls.AddRange([_myHeading, _myEmpty, _myRow, _favHeading, _favRow]);
        foreach (var (category, scenes) in ScenePresets.Categories)
        {
            var heading = CategoryLabel(category.ToUpperInvariant());
            // One drawn control per category instead of a Button per preset: the page appears several times faster.
            var row = PresetRow();
            presets.Controls.AddRange([heading, row]);
            _categories.Add((heading, row, scenes));
        }
        presets.Controls.Add(_noMatch);
        foreach (var row in PresetRows())
        {
            row.ChipClicked += LoadPreset;
            row.ChipMenu += PresetMenu;
        }
        Heading("Presets");
        var surprise = Ui.Button("Surprise me", (_, _) => SurpriseMe());
        var import = Ui.Button("Import…", (_, _) => ImportScenes());
        _tips.SetToolTip(surprise, "Load a random preset");
        _tips.SetToolTip(import, "Add scenes someone shared with you (" + SceneFiles.Extension + " files) to My presets");
        AddFull(Pair(Hint("Search"), _search, surprise, import));
        AddFull(_presetStatus);
        AddFull(presets);
        _search.TextChanged += (_, _) => RefreshPresets();

        Heading("Your scene");
        var save = Ui.Button("Save as my preset…", (_, _) => SaveAsMine(), primary: true);
        _undoButton = Ui.Button("Undo", (_, _) => Undo());
        _redoButton = Ui.Button("Redo", (_, _) => Redo());
        var remix = Ui.Button("Remix colours", (_, _) => RemixColours());
        var export = Ui.Button("Export…", (_, _) => Export([_scene], _scene.Name));
        _tips.SetToolTip(save, "Keep this scene in My presets, to load it again any time");
        _tips.SetToolTip(_undoButton, "Undo the last change to the scene (Ctrl+Z)");
        _tips.SetToolTip(_redoButton, "Redo (Ctrl+Y)");
        _tips.SetToolTip(remix, "New colours from a random colour scheme; every colour keeps its brightness. Click again for another.");
        _tips.SetToolTip(export, "Save this scene as a file to share it");
        AddFull(Pair(save, _undoButton, _redoButton, remix, export));
        AddFull(_sceneStatus);
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

        _effects.SetChips(SceneEffects.All.Where(i => i.Effect != SceneEffect.PerKey).Select(i => new ChipPicker.Chip(i.Name, i.Description, i.Effect)));
        _effects.ChipClicked += chip => SetEffect((SceneEffect)chip.Tag!);
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
        RefreshPresets();
        UpdateUndoButtons();
        // The studio can sit inside another page, so it watches its own visibility instead of relying on VisibleChanged.
        _previewTimer.Tick += (_, _) => Preview();
        _previewTimer.Start();
        Disposed += (_, _) => { _previewTimer.Dispose(); _tips.Dispose(); };
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
        // Without the real keyboard the preview plays pretend typing, music, mouse and screen events, so reactive effects show.
        var frame = live is { Count: > 0 } ? live : SceneRenderer.Render(_scene, SceneDemo.Context(_clock.Elapsed.TotalSeconds, _previewLayout));
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
        NewUndoStep();
        if (Current is not { Effect: SceneEffect.PerKey } l) return;
        Paint(l, AllIds());
        Commit();
    }

    void ClearPaint()
    {
        NewUndoStep();
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

    /// <summary>Shows the selected layer's settings; rows appear and disappear in one layout pass.</summary>
    void ShowLayer() => Ui.Batch(this, ShowLayerCore);

    void ShowLayerCore()
    {
        var l = Current;
        _loading = true;
        bool paint = l?.Effect == SceneEffect.PerKey;
        _effects.Selected = l?.Effect;
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
        NewUndoStep();
        if (Current is not { } l || l.Colors.Count >= 7 || !SceneEffects.Get(l.Effect).ColorModes.Contains(SceneColorMode.Gradient)) return;
        l.ColorMode = SceneColorMode.Gradient;
        l.Colors.Add(l.Colors.LastOrDefault() ?? "FFFFFF");
        FixColors(l);
        ShowLayer();
        Commit();
    }

    void RemoveColor()
    {
        NewUndoStep();
        if (Current is not { } l || l.Colors.Count <= 1) return;
        l.Colors.RemoveAt(l.Colors.Count - 1);
        l.ColorMode = l.Colors.Count switch { 1 => SceneColorMode.Single, 2 when l.ColorMode == SceneColorMode.Gradient => SceneColorMode.Gradient, _ => l.ColorMode };
        FixColors(l);
        ShowLayer();
        Commit();
    }

    void SetEffect(SceneEffect effect)
    {
        NewUndoStep();
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
        NewUndoStep();
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
        NewUndoStep();
        if (Current is null) return;
        _scene.Layers.RemoveAt(_layers.SelectedIndex);
        RefreshLayers();
        Commit();
    }

    void MoveLayer(int delta)
    {
        NewUndoStep();
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
        NewUndoStep();
        if (Current is not { } l) return;
        l.Enabled = !l.Enabled;
        RefreshLayers();
        Commit();
    }

    void RenameLayer()
    {
        NewUndoStep();
        if (Current is not { } l) return;
        if (Ui.Prompt(FindForm(), "Rename layer", "Layer name", l.Name) is not { } name || string.IsNullOrWhiteSpace(name)) return;
        l.Name = name.Trim();
        RefreshLayers();
        Commit();
    }

    void LoadPreset(ChipPicker.Chip chip)
    {
        if (chip.Tag is PresetRef r && Find(r) is { } scene) LoadPreset(scene, r);
    }

    void LoadPreset(LightingScene preset, PresetRef? from)
    {
        NewUndoStep();
        var copy = preset.Clone();
        _scene.Name = copy.Name;
        _scene.Description = copy.Description;
        _scene.Background = copy.Background;
        _scene.Layers = copy.Layers;
        _layers.SelectedIndex = -1;
        _loaded = from;
        ShowSelection();
        RefreshLayers();
        _presetStatus.Text = string.IsNullOrWhiteSpace(copy.Description) ? $"\"{copy.Name}\"" : $"\"{copy.Name}\" — {copy.Description}";
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
        _lastScene = JsonSerializer.Serialize(_scene); // changed elsewhere: start a fresh undo history
        _undo.Clear();
        _redo.Clear();
        UpdateUndoButtons();
        _loaded = SourceOf(_scene.Name);
        RefreshLayers();
        RefreshPresets();
    }

    /// <summary>Applies the edited scene and options immediately (and saves them). Scene edits make the studio active.</summary>
    void Commit(bool activate = true)
    {
        if (_loading) return;
        TrackUndo();
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

    // ---------------------------------------------------------------- presets library

    static Label CategoryLabel(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Ui.Dim, Font = new Font("Segoe UI Semibold", 8.5f),
        Margin = new Padding(2, 8, 0, 2), UseMnemonic = false,
    };

    static ChipPicker PresetRow() => new(wrapWidth: 1000, minChipWidth: 124) { Margin = new Padding(0, 0, 0, 2) };

    IEnumerable<ChipPicker> PresetRows() => new[] { _myRow, _favRow }.Concat(_categories.Select(c => c.Row));

    static ChipPicker.Chip ChipFor(LightingScene p, bool mine) =>
        new(p.Name, string.IsNullOrWhiteSpace(p.Description) ? null : p.Description, new PresetRef(p.Name, mine));

    List<LightingScene> MyScenes() => _get().CustomScenes ?? [];

    LightingScene? Find(PresetRef r) => r.Mine ? MyScenes().FirstOrDefault(c => c.Name == r.Name) : _builtIn.GetValueOrDefault(r.Name);

    PresetRef? SourceOf(string name) =>
        MyScenes().Any(c => c.Name == name) ? new(name, true) : _builtIn.ContainsKey(name) ? new(name, false) : null;

    void ShowSelection()
    {
        foreach (var row in PresetRows()) row.Selected = _loaded;
    }

    /// <summary>Fills My presets and Favourites and applies the search, all in one layout pass.</summary>
    void RefreshPresets() => Ui.Batch(this, () =>
    {
        string q = _search.Text.Trim();
        bool Match(LightingScene p) => q.Length == 0
            || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Layers.Any(l => SceneEffects.Get(l.Effect).Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        var s = _get();
        var mine = (s.CustomScenes ?? []).Where(Match).ToList();
        _myRow.SetChips(mine.Select(p => ChipFor(p, mine: true)));
        _myRow.Visible = mine.Count > 0;
        _myEmpty.Visible = q.Length == 0 && (s.CustomScenes ?? []).Count == 0;
        _myHeading.Visible = _myRow.Visible || _myEmpty.Visible;
        var favourites = (s.FavoriteScenes ?? []).Select(n => _builtIn.GetValueOrDefault(n)).OfType<LightingScene>().Where(Match).ToList();
        _favRow.SetChips(favourites.Select(p => ChipFor(p, mine: false)));
        _favHeading.Visible = _favRow.Visible = favourites.Count > 0;
        int shown = mine.Count + favourites.Count;
        foreach (var (heading, row, scenes) in _categories)
        {
            var match = scenes.Where(Match).ToList();
            if (!row.Chips.Select(c => ((PresetRef)c.Tag!).Name).SequenceEqual(match.Select(p => p.Name)))
                row.SetChips(match.Select(p => ChipFor(p, mine: false)));
            heading.Visible = row.Visible = match.Count > 0;
            shown += match.Count;
        }
        _noMatch.Visible = q.Length > 0 && shown == 0;
        ShowSelection();
    });

    void PresetMenu(ChipPicker.Chip chip, Point at)
    {
        if (chip.Tag is not PresetRef r || Find(r) is not { } scene) return;
        var menu = new ContextMenuStrip { Font = Ui.Body };
        menu.Items.Add("Load", null, (_, _) => LoadPreset(scene, r));
        if (r.Mine)
        {
            menu.Items.Add("Update with the scene below", null, (_, _) => UpdateMine(r.Name));
            menu.Items.Add("Rename…", null, (_, _) => RenameMine(r.Name));
            menu.Items.Add("Export…", null, (_, _) => Export([scene], scene.Name));
            menu.Items.Add("Export all my presets…", null, (_, _) => Export(MyScenes(), "My OverMount presets"));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Delete", null, (_, _) => DeleteMine(r.Name));
        }
        else
        {
            bool favourite = (_get().FavoriteScenes ?? []).Contains(r.Name);
            menu.Items.Add(favourite ? "Remove from Favourites" : "Add to Favourites", null, (_, _) => ToggleFavourite(r.Name));
            menu.Items.Add("Save a copy to My presets…", null, (_, _) => SaveAsMine(scene));
            menu.Items.Add("Export…", null, (_, _) => Export([scene], scene.Name));
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose); // after the clicked item's handler has run
        menu.Show(at);
    }

    /// <summary>Changes the saved library (My presets, Favourites) and refreshes the lists.</summary>
    void UpdateLibrary(Action<AppSettings> change)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_get()))!;
        s.CustomScenes ??= [];
        s.FavoriteScenes ??= [];
        change(s);
        _apply(s);
        RefreshPresets();
    }

    static int IndexOf(List<LightingScene> scenes, string name) =>
        scenes.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Saves the scene below (or <paramref name="source"/>, a built-in preset) in My presets under a name the user picks.</summary>
    void SaveAsMine(LightingScene? source = null)
    {
        var scene = (source ?? _scene).Clone();
        if (scene.Layers.Count == 0) { _sceneStatus.Text = "Pick a preset or add a layer first: there's nothing to save yet."; return; }
        var mine = MyScenes();
        bool editingMine = source is null && _loaded is { Mine: true } l && l.Name == scene.Name;
        string suggestion = editingMine ? scene.Name
            : SceneFiles.UniqueName(_builtIn.ContainsKey(scene.Name) ? SceneFiles.CleanName("My " + scene.Name) : SceneFiles.CleanName(scene.Name),
                mine.Select(m => m.Name));
        if (Ui.Prompt(FindForm(), "Save as my preset", "Name for your preset", suggestion) is not { } typed) return;
        var name = SceneFiles.CleanName(typed);
        if (name.Length == 0) return;
        int existing = IndexOf(mine, name);
        if (existing < 0 && mine.Count >= SceneFiles.MaxScenes)
        {
            _sceneStatus.Text = $"My presets is full ({SceneFiles.MaxScenes}). Delete one first (right-click it).";
            return;
        }
        if (existing >= 0 && MessageBox.Show(FindForm(), $"Replace your preset \"{mine[existing].Name}\" with this scene?", "OverMount",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        scene.Name = name;
        if (string.IsNullOrWhiteSpace(scene.Description)) scene.Description = "Your own scene.";
        if (source is null)
        {
            _scene.Name = name; // the scene below is now this preset
            _lastScene = JsonSerializer.Serialize(_scene);
            _loaded = new PresetRef(name, true);
        }
        UpdateLibrary(s =>
        {
            int i = IndexOf(s.CustomScenes, name);
            if (i >= 0) s.CustomScenes[i] = scene;
            else s.CustomScenes.Add(scene);
            if (source is null) s.Scene = _scene.Clone();
        });
        (source is null ? _sceneStatus : _presetStatus).Text = $"Saved \"{name}\" in My presets (at the top of Presets). Right-click it to rename, update, export or delete it.";
    }

    void UpdateMine(string name)
    {
        if (_scene.Layers.Count == 0) return;
        if (MessageBox.Show(FindForm(), $"Replace \"{name}\" with the scene below?", "OverMount",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        UpdateLibrary(s =>
        {
            int i = IndexOf(s.CustomScenes, name);
            if (i < 0) return;
            var copy = _scene.Clone();
            copy.Name = s.CustomScenes[i].Name;
            copy.Description = s.CustomScenes[i].Description;
            s.CustomScenes[i] = copy;
        });
        _presetStatus.Text = $"\"{name}\" now holds the scene below.";
    }

    void RenameMine(string name)
    {
        if (Ui.Prompt(FindForm(), "Rename preset", "New name", name) is not { } typed) return;
        var newName = SceneFiles.CleanName(typed);
        if (newName.Length == 0 || newName == name) return;
        if (IndexOf(MyScenes(), newName) is var clash && clash >= 0 && !string.Equals(MyScenes()[clash].Name, name, StringComparison.OrdinalIgnoreCase))
        {
            _presetStatus.Text = $"You already have a preset called \"{MyScenes()[clash].Name}\".";
            return;
        }
        bool current = _loaded == new PresetRef(name, true);
        if (current)
        {
            _scene.Name = newName;
            _lastScene = JsonSerializer.Serialize(_scene);
            _loaded = new PresetRef(newName, true);
        }
        UpdateLibrary(s =>
        {
            int i = IndexOf(s.CustomScenes, name);
            if (i >= 0) s.CustomScenes[i].Name = newName;
            if (current) s.Scene = _scene.Clone();
        });
        _presetStatus.Text = $"Renamed to \"{newName}\".";
    }

    void DeleteMine(string name)
    {
        if (MessageBox.Show(FindForm(), $"Delete your preset \"{name}\"? This can't be undone (the lighting on your keyboard stays as it is).",
                "OverMount", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (_loaded == new PresetRef(name, true)) _loaded = null;
        UpdateLibrary(s => s.CustomScenes.RemoveAll(c => c.Name == name));
        _presetStatus.Text = $"Deleted \"{name}\".";
    }

    void ToggleFavourite(string name)
    {
        bool add = !(_get().FavoriteScenes ?? []).Contains(name);
        UpdateLibrary(s =>
        {
            s.FavoriteScenes.Remove(name);
            if (add) s.FavoriteScenes.Add(name);
        });
        _presetStatus.Text = add ? $"\"{name}\" is in your Favourites." : $"\"{name}\" was removed from your Favourites.";
    }

    void SurpriseMe()
    {
        var pool = _builtIn.Values.Select(p => (Scene: p, Ref: new PresetRef(p.Name, false)))
            .Concat(MyScenes().Select(p => (Scene: p, Ref: new PresetRef(p.Name, true))))
            .Where(x => x.Ref != _loaded).ToList();
        if (pool.Count == 0) return;
        var (scene, r) = pool[_random.Next(pool.Count)];
        LoadPreset(scene, r);
    }

    static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return clean.Length == 0 ? "Scene" : clean;
    }

    void Export(IEnumerable<LightingScene> scenes, string fileName)
    {
        var list = scenes.Where(s => s.Layers.Count > 0).ToList();
        if (list.Count == 0) { _sceneStatus.Text = "There's nothing to export yet."; return; }
        using var dlg = new SaveFileDialog
        {
            Title = "Export lighting", FileName = SafeFileName(fileName) + SceneFiles.Extension, AddExtension = false,
            Filter = "OverMount scenes|*" + SceneFiles.Extension + "|All files|*.*", OverwritePrompt = true,
        };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, SceneFiles.Serialize(list));
            _sceneStatus.Text = $"Exported {(list.Count == 1 ? $"\"{list[0].Name}\"" : $"{list.Count} scenes")} to {Path.GetFileName(dlg.FileName)}. " +
                                "Anyone with OverMount can add it with Presets → Import.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _sceneStatus.Text = $"Export failed: {e.Message}";
        }
    }

    void ImportScenes()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Import lighting", Multiselect = true, Filter = "OverMount scenes|*.json|All files|*.*",
        };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        var found = new List<LightingScene>();
        var problems = new List<string>();
        long total = 0;
        foreach (var path in dlg.FileNames)
        {
            try
            {
                long size = new FileInfo(path).Length;
                if (size > SceneFiles.MaxFileBytes || (total += size) > 4L * SceneFiles.MaxFileBytes)
                {
                    problems.Add($"{Path.GetFileName(path)}: too big");
                    continue;
                }
                found.AddRange(SceneFiles.Parse(File.ReadAllText(path)));
            }
            catch (FormatException e) { problems.Add($"{Path.GetFileName(path)}: {e.Message}"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problems.Add($"{Path.GetFileName(path)}: {e.Message}"); }
        }
        if (found.Count == 0)
        {
            MessageBox.Show(FindForm(), "Nothing was imported.\n\n" + string.Join("\n", problems), "OverMount",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var added = new List<LightingScene>();
        UpdateLibrary(s =>
        {
            foreach (var scene in found)
            {
                if (s.CustomScenes.Count >= SceneFiles.MaxScenes) { problems.Add("My presets is full"); break; }
                scene.Name = SceneFiles.UniqueName(scene.Name, s.CustomScenes.Select(c => c.Name));
                s.CustomScenes.Add(scene);
                added.Add(scene);
            }
        });
        if (added.Count > 0) LoadPreset(added[0], new PresetRef(added[0].Name, true));
        _presetStatus.Text = $"Imported {added.Count} scene{(added.Count == 1 ? "" : "s")} into My presets" +
                             (problems.Count > 0 ? $". Skipped: {string.Join("; ", problems.Distinct())}" : ".");
    }

    // ---------------------------------------------------------------- undo and remix

    void TrackUndo()
    {
        var json = JsonSerializer.Serialize(_scene);
        if (json == _lastScene) return;
        if (!_restoring)
        {
            long now = Environment.TickCount64;
            if (now - _lastUndoPush > 600)
            {
                _undo.Add(_lastScene);
                if (_undo.Count > 60) _undo.RemoveAt(0);
            }
            _lastUndoPush = now;
            _redo.Clear();
        }
        _lastScene = json;
        UpdateUndoButtons();
    }

    /// <summary>Dimmed rather than disabled: a disabled flat button's text is barely readable on the dark theme.</summary>
    /// <summary>The next change is its own undo step (only continuous edits such as dragging a slider are merged).</summary>
    void NewUndoStep() => _lastUndoPush = 0;

    void UpdateUndoButtons()
    {
        _undoButton.ForeColor = _undo.Count > 0 ? Ui.Text : Ui.Dim;
        _redoButton.ForeColor = _redo.Count > 0 ? Ui.Text : Ui.Dim;
    }

    void Undo() => Step(_undo, _redo, "Undone");

    void Redo() => Step(_redo, _undo, "Redone");

    void Step(List<string> from, List<string> to, string what)
    {
        if (from.Count == 0) { _sceneStatus.Text = from == _undo ? "Nothing to undo yet." : "Nothing to redo."; return; }
        to.Add(JsonSerializer.Serialize(_scene));
        var snapshot = JsonSerializer.Deserialize<LightingScene>(from[^1])!;
        from.RemoveAt(from.Count - 1);
        _scene.Name = snapshot.Name;
        _scene.Description = snapshot.Description;
        _scene.Background = snapshot.Background;
        _scene.Layers = snapshot.Layers;
        _restoring = true;
        try
        {
            RefreshLayers();
            Commit();
        }
        finally { _restoring = false; }
        _loaded = SourceOf(_scene.Name);
        ShowSelection();
        UpdateUndoButtons();
        _sceneStatus.Text = $"{what}. {_undo.Count} step{(_undo.Count == 1 ? "" : "s")} to undo, {_redo.Count} to redo.";
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (FromHandle(msg.HWnd) is not TextBoxBase) // text boxes keep their own Ctrl+Z
        {
            if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
            if (keyData is (Keys.Control | Keys.Y) or (Keys.Control | Keys.Shift | Keys.Z)) { Redo(); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // Hue offsets (turns) of a few classic colour schemes.
    static readonly (string Name, double[] Hues)[] Schemes =
    [
        ("analogous", [0, 0.07, -0.07, 0.14, -0.14]),
        ("complementary", [0, 0.5, 0.04, 0.54, 0.46]),
        ("triadic", [0, 1 / 3.0, 2 / 3.0, 0.06, 0.39]),
        ("split-complementary", [0, 0.42, 0.58, 0.05, 0.5]),
        ("tetradic", [0, 0.25, 0.5, 0.75, 0.125]),
    ];

    /// <summary>
    /// New hues from a random colour scheme for every colour in the scene; each keeps its saturation and brightness, so
    /// dark backgrounds stay dark and whites stay white. The same colour gets the same new colour on every layer.
    /// </summary>
    void RemixColours()
    {
        NewUndoStep();
        if (_scene.Layers.Count == 0) { _sceneStatus.Text = "Pick a preset or add a layer first."; return; }
        var (scheme, hues) = Schemes[_random.Next(Schemes.Length)];
        double baseHue = _random.NextDouble();
        int next = 0;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Remap(string hex)
        {
            if (map.TryGetValue(hex, out var done)) return done;
            var (_, sat, val) = ToHsv(ToColor(hex));
            return map[hex] = sat < 0.12 || val < 0.03 ? hex : Hex(FromHsv(baseHue + hues[next++ % hues.Length], sat, val));
        }
        foreach (var l in _scene.Layers)
        {
            l.Colors = [.. l.Colors.Select(Remap)];
            foreach (var k in l.KeyColors.Keys.ToList()) l.KeyColors[k] = Remap(l.KeyColors[k]);
            foreach (var k in l.EdgeColors.Keys.ToList()) l.EdgeColors[k] = Remap(l.EdgeColors[k]);
        }
        ShowLayer();
        Commit();
        _sceneStatus.Text = $"Remixed with a {scheme} colour scheme. Click again for another, or Undo (Ctrl+Z) for the old colours.";
    }

    static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return ((h / 6 % 1 + 1) % 1, max == 0 ? 0 : d / max, max);
    }

    static Color FromHsv(double h, double s, double v)
    {
        h = (h % 1 + 1) % 1 * 6;
        int i = (int)Math.Floor(h) % 6;
        double f = h - Math.Floor(h), p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return Color.FromArgb((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
    }

    static Color ToColor(string hex)
    {
        try { var c = Rgb.Parse(hex); return Color.FromArgb(c.R, c.G, c.B); }
        catch (Exception e) when (e is FormatException or ArgumentException) { return Color.OrangeRed; }
    }

    static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";
}
