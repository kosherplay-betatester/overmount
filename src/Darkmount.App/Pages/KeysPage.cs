using Darkmount.Keyboard;
using static Darkmount.Keyboard.BindingAction;

namespace Darkmount.App.Pages;

/// <summary>
/// Key remapping on the base and Fn layers (every key, the 8 display keys and the 4 dock buttons), the master
/// binding switch, and the Game Mode key locks. Bindings are stored on the keyboard. Gamer-style layout: a large
/// keyboard that fills the window, remapped keys labelled on the keycaps, and action tiles.
/// </summary>
public sealed class KeysPage : Ui.Page
{
    enum Kind { Default, Disabled, Key, FKey, Media, Mouse, Scroll, WindowsShortcut, Backlight, Character, Website }

    // Segoe MDL2 / Fluent icon glyphs.
    static readonly (Kind Kind, char Glyph, string Name)[] Kinds =
    [
        (Kind.Default, '', "Default"), (Kind.Key, '', "Key / combo"), (Kind.Media, '', "Media"),
        (Kind.Mouse, '', "Mouse"), (Kind.Scroll, '', "Scroll"), (Kind.FKey, '', "Macro key"),
        (Kind.WindowsShortcut, '', "Windows"), (Kind.Backlight, '', "Lighting"), (Kind.Character, '', "Character"),
        (Kind.Website, '', "Website"), (Kind.Disabled, '', "Disable"),
    ];

    readonly KeyboardService _keyboard;
    readonly KeyboardView _view = new() { Size = new Size(900, 330), Margin = new Padding(0, 6, 0, 10) };
    readonly Button _baseLayer, _fnLayer;
    readonly ToggleSwitch _enabled = new("Custom key bindings ON");
    readonly Label _keyTitle = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 20f), ForeColor = Color.White, Margin = new Padding(0, 0, 0, 2) };
    readonly Label _current = new() { AutoSize = true, Font = Ui.Body, ForeColor = Color.FromArgb(255, 170, 70), MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 10) };
    readonly FlowLayoutPanel _tiles = new() { AutoSize = true, WrapContents = true, MaximumSize = new Size(580, 0) };
    readonly FlowLayoutPanel _editor = new() { AutoSize = true, WrapContents = true, MaximumSize = new Size(580, 0), Margin = new Padding(0, 4, 0, 8) };
    readonly Label _status = Ui.Note("", 900);
    readonly Dictionary<Kind, IconTile> _tileOf = [];
    Kind _kind = Kind.Default;
    bool _fnSelected;

    // Editor controls (shown depending on the kind).
    readonly CheckBox _ctrl = Ui.Check("Ctrl"), _shift = Ui.Check("Shift"), _alt = Ui.Check("Alt"), _win = Ui.Check("Win");
    readonly ComboBox _usage = Combo(220), _fkey = Combo(120), _media = Combo(200), _mouse = Combo(160),
        _scroll = Combo(160), _shortcut = Combo(220), _backlight = Combo(220), _effect = Combo(160);
    readonly CheckBox _double = Ui.Check("Double click"), _hold = Ui.Check("While pressed");
    readonly NumericUpDown _autoFire = Ui.Number(0, 50);
    readonly TextBox _text = new() { Width = 360, Font = Ui.Body };

    // Game Mode locks
    readonly ToggleSwitch _lockWin = new("Windows key"), _lockAltTab = new("Alt+Tab"), _lockAltF4 = new("Alt+F4"),
        _lockShiftTab = new("Shift+Tab"), _lockCaps = new("Caps Lock"), _gameMode = new("GAME MODE ACTIVE");

    List<KeyBinding> _bindings = [];
    bool _loaded;

    public KeysPage(KeyboardService keyboard)
        : base("Key bindings", "Click any key, pick what it should do, hit APPLY. Stored on the keyboard — works everywhere, " +
                               "even without OverMount. Dock buttons use these in the dock's CUSTOM mode.")
    {
        _keyboard = keyboard;
        _view.KeyShapes = KeyGeometry.Layout(PhysicalLayout.Ansi, NumpadSide.Right, includeDock: true)
            .Select(k => new KeyShape(k.KeyId, new RectangleF(k.X, k.Y, k.Width, k.Height), ShortLabel(k.KeyId)))
            .ToList();
        _view.SelectionChanged += ShowSelected;

        _baseLayer = LayerButton("BASE LAYER", false);
        _fnLayer = LayerButton("FN LAYER", true);
        var layers = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
        layers.Controls.AddRange([_baseLayer, _fnLayer, Spacer(24), _enabled]);
        _enabled.Size = new Size(260, 40);
        AddFull(layers);
        AddFull(_view);

        foreach (var (kind, glyph, name) in Kinds)
        {
            var tile = new IconTile(glyph, name);
            tile.Click += (_, _) => SelectKind(kind);
            _tileOf[kind] = tile;
            _tiles.Controls.Add(tile);
        }
        foreach (var u in HidUsage.Offered) _usage.Items.Add(new Item<byte>(u.Name, u.Usage));
        for (int n = 13; n <= 24; n++) _fkey.Items.Add(new Item<byte>($"F{n}", HidUsage.FKey(n)));
        foreach (var m in Enum.GetValues<MediaAction>().Where(m => m is not (MediaAction.None or MediaAction.SpecificSound)))
            _media.Items.Add(new Item<MediaAction>(Words(m.ToString()), m));
        foreach (var b in Enum.GetValues<MouseButtonKind>()) _mouse.Items.Add(new Item<MouseButtonKind>(Words(b.ToString()), b));
        foreach (var s in Enum.GetValues<MouseScrollDirection>()) _scroll.Items.Add(new Item<MouseScrollDirection>(Words(s.ToString()), s));
        foreach (var w in Enum.GetValues<WindowsShortcutAction>().Where(w => w != WindowsShortcutAction.None))
            _shortcut.Items.Add(new Item<WindowsShortcutAction>(Words(w.ToString()), w));
        foreach (var a in Enum.GetValues<BacklightAction>().Where(a => a != BacklightAction.None))
            _backlight.Items.Add(new Item<BacklightAction>(Words(a.ToString()), a));
        foreach (var e in LightingEffects.DarkMount) _effect.Items.Add(new Item<Effect>(e.Name, e.Effect));
        _backlight.SelectedIndexChanged += (_, _) => _effect.Visible = Selected<BacklightAction>(_backlight) == BacklightAction.SelectEffect;

        // Selected-key card
        var keyCard = new Card("Selected key");
        var apply = Ui.Button("APPLY TO KEY", async (_, _) => await ApplyKey(), primary: true);
        apply.MinimumSize = new Size(170, 42);
        apply.Font = new Font("Segoe UI Semibold", 11f);
        var keyActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        keyActions.Controls.AddRange([apply, Ui.Button("Reload", async (_, _) => await Load()),
            Ui.Button("Factory bindings", async (_, _) => await RestoreFactory())]);
        keyCard.Controls.AddRange([_keyTitle, _current, _tiles, _editor, keyActions]);

        // Game Mode card
        var gameCard = new Card("Game Mode  ·  Fn + Pause");
        gameCard.Controls.Add(new Label
        {
            Text = "While Game Mode is on, these are blocked so you\ncan't alt-tab out of a match by accident:",
            AutoSize = true, ForeColor = Ui.Dim, Font = Ui.Body, Margin = new Padding(0, 0, 0, 8),
        });
        foreach (var t in new[] { _lockWin, _lockAltTab, _lockAltF4, _lockShiftTab, _lockCaps }) { t.Size = new Size(260, 32); gameCard.Controls.Add(t); }
        _gameMode.Size = new Size(260, 40);
        _gameMode.Font = new Font("Segoe UI Semibold", 10.5f);
        gameCard.Controls.Add(_gameMode);
        var applyGm = Ui.Button("APPLY GAME MODE", async (_, _) => await ApplyGameMode(), primary: true);
        applyGm.MinimumSize = new Size(200, 40);
        gameCard.Controls.Add(applyGm);

        var cards = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        cards.Controls.AddRange([keyCard, gameCard]);
        AddFull(cards);
        AddFull(_status);

        _enabled.Click += async (_, _) => await ApplyEnabled();
        SetLayer(false);
        _view.Select([KeyIds.CapsLock]);
        SelectKind(Kind.Default);
        Control? sizedBy = null;
        ParentChanged += (_, _) =>
        {
            if (sizedBy is not null) sizedBy.SizeChanged -= OnParentResized;
            sizedBy = Parent;
            if (sizedBy is null) return;
            sizedBy.SizeChanged += OnParentResized;
            FitKeyboard();
        };
        VisibleChanged += async (_, _) => { if (Visible && !_loaded) await Load(); };
    }

    void OnParentResized(object? sender, EventArgs e) => FitKeyboard();

    /// <summary>The keyboard fills the page width (and keeps its proportions).</summary>
    void FitKeyboard()
    {
        if (Parent is null) return;
        // Pixel numbers here are 96-DPI units, converted for the screen (the window's own scaling doesn't reach runtime code).
        int width = Math.Max(LogicalToDeviceUnits(640),
            Parent.ClientSize.Width - Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - LogicalToDeviceUnits(8));
        _view.Size = new Size(width, (int)(width * 0.38));
    }

    Button LayerButton(string text, bool fn)
    {
        var b = Ui.Button(text, (_, _) => SetLayer(fn));
        b.MinimumSize = new Size(170, 42);
        b.Font = new Font("Segoe UI Semibold", 11f);
        return b;
    }

    static Control Spacer(int w) => new Panel { Width = w, Height = 1, Margin = new Padding(0) };

    void SetLayer(bool fn)
    {
        _fnSelected = fn;
        foreach (var (b, on) in new[] { (_baseLayer, !fn), (_fnLayer, fn) })
        {
            b.BackColor = on ? Ui.Accent : Ui.Panel;
            b.ForeColor = on ? Color.Black : Ui.Text;
        }
        RefreshMarks();
        ShowSelected();
    }

    Layer CurrentLayer => _fnSelected ? Layer.Fn : Layer.Common;

    // ---------------------------------------------------------------- loading

    async Task Load()
    {
        _loaded = true;
        _status.Text = "Reading the keyboard's bindings…";
        try
        {
            var (bindings, enabled, locks, state) = await _keyboard.Run(q =>
            {
                var s = new KeyboardSettings(q);
                return (new Bindings(q).GetAll().ToList(), new Bindings(q).GetEnabled(), s.GetLocks(), s.GetState());
            });
            if (IsDisposed) return;
            _bindings = bindings;
            _enabled.Checked = enabled;
            _lockWin.Checked = locks.HasFlag(GameModeLocks.Win);
            _lockAltTab.Checked = locks.HasFlag(GameModeLocks.AltTab);
            _lockAltF4.Checked = locks.HasFlag(GameModeLocks.AltF4);
            _lockShiftTab.Checked = locks.HasFlag(GameModeLocks.ShiftTab);
            _lockCaps.Checked = locks.HasFlag(GameModeLocks.CapsLock);
            _gameMode.Checked = state.HasFlag(KeyboardStateFlags.GameMode);
            RefreshMarks();
            ShowSelected();
            _status.Text = $"{_bindings.Count} custom binding(s) on the keyboard. Remapped keys show their new function in orange.";
        }
        catch (Exception e) { _status.Text = Friendly(e); }
    }

    void RefreshMarks()
    {
        var layer = _bindings.Where(b => b.Layer == CurrentLayer).ToList();
        _view.SetMarked(layer.Select(b => (int)b.KeyId));
        _view.SetSubLabels(layer.ToDictionary(b => (int)b.KeyId, b => Short(b.Action)));
    }

    // ---------------------------------------------------------------- selected key

    byte? SelectedKey => _view.SelectedKeys.Count == 1 ? (byte)_view.SelectedKeys.First() : null;

    void ShowSelected()
    {
        if (SelectedKey is not { } id || KeyIds.Find(id) is not { } info) return;
        _keyTitle.Text = $"{info.Label}{(CurrentLayer == Layer.Fn ? "  (Fn +)" : "")}";
        bool locked = !KeyIds.IsRebindable(id, CurrentLayer);
        var binding = _bindings.FirstOrDefault(b => b.KeyId == id && b.Layer == CurrentLayer);
        _current.Text = locked ? "Locked: this key controls the keyboard itself and can't be changed."
            : binding is null ? "Does its normal job." : $"Now does: {Describe(binding.Action)}";
        _tiles.Enabled = _editor.Enabled = !locked;
        LoadAction(binding?.Action);
    }

    void LoadAction(BindingAction? action)
    {
        var kind = action switch
        {
            null => Kind.Default,
            Disabled => Kind.Disabled,
            StandardKey => Kind.Key,
            FKey f when f.Number >= 13 => Kind.FKey,
            FKey => Kind.Key,
            Media => Kind.Media,
            MouseButton => Kind.Mouse,
            MouseScroll => Kind.Scroll,
            WindowsShortcut => Kind.WindowsShortcut,
            Backlight => Kind.Backlight,
            AltCode => Kind.Character,
            OpenBrowser => Kind.Website,
            _ => Kind.Default,
        };
        switch (action)
        {
            case StandardKey k:
                (_ctrl.Checked, _shift.Checked, _alt.Checked, _win.Checked) = (
                    (k.Modifiers & (KeyModifiers.LeftCtrl | KeyModifiers.RightCtrl)) != 0,
                    (k.Modifiers & (KeyModifiers.LeftShift | KeyModifiers.RightShift)) != 0,
                    (k.Modifiers & (KeyModifiers.LeftAlt | KeyModifiers.RightAlt)) != 0,
                    (k.Modifiers & (KeyModifiers.LeftWin | KeyModifiers.RightWin)) != 0);
                SelectValue(_usage, k.Usage);
                break;
            case FKey f when f.Number >= 13: SelectValue(_fkey, f.Usage); break;
            case FKey f: (_ctrl.Checked, _shift.Checked, _alt.Checked, _win.Checked) = (false, false, false, false); SelectValue(_usage, f.Usage); break;
            case Media m: SelectValue(_media, m.Action); break;
            case MouseButton b: SelectValue(_mouse, b.Button); _double.Checked = b.DoubleClick; _hold.Checked = b.WhilePressed; _autoFire.Value = Math.Min(50, (int)b.AutoFire); break;
            case MouseScroll s: SelectValue(_scroll, s.Direction); break;
            case WindowsShortcut w: SelectValue(_shortcut, w.Action); break;
            case Backlight b: SelectValue(_backlight, b.Action); if (b.Effect is { } e) SelectValue(_effect, e); break;
            case AltCode a: _text.Text = char.ConvertFromUtf32(a.CodePoint); break;
            case OpenBrowser o: _text.Text = o.Url; break;
        }
        SelectKind(kind);
    }

    void SelectKind(Kind kind)
    {
        _kind = kind;
        foreach (var (k, tile) in _tileOf) tile.Selected = k == kind;
        _editor.Controls.Clear();
        switch (kind)
        {
            case Kind.Default: Ui.Add(_editor, Hint("The key goes back to its normal function.")); break;
            case Kind.Disabled: Ui.Add(_editor, Hint("The key does nothing (great for the Windows key in games).")); break;
            case Kind.Key: Ui.AddRange(_editor, _ctrl, _shift, _alt, _win, _usage); break;
            case Kind.FKey: Ui.AddRange(_editor, _fkey, Hint("Triggers a macro from the Macros page.")); break;
            case Kind.Media: Ui.Add(_editor, _media); break;
            case Kind.Mouse:
                Ui.AddRange(_editor, _mouse, _double, _hold, Hint("Auto-fire clicks/s (0 = off)"), _autoFire);
                break;
            case Kind.Scroll: Ui.Add(_editor, _scroll); break;
            case Kind.WindowsShortcut: Ui.Add(_editor, _shortcut); break;
            case Kind.Backlight: Ui.AddRange(_editor, _backlight, _effect); _effect.Visible = Selected<BacklightAction>(_backlight) == BacklightAction.SelectEffect; break;
            case Kind.Character: Ui.AddRange(_editor, _text, Hint("Type the character, e.g. € or ©.")); break;
            case Kind.Website: Ui.AddRange(_editor, _text, Hint("Full address, e.g. https://twitch.tv")); break;
        }
        foreach (var c in new ComboBox[] { _usage, _fkey, _media, _mouse, _scroll, _shortcut, _backlight, _effect })
            if (c.SelectedIndex < 0 && c.Items.Count > 0) c.SelectedIndex = 0;
    }

    static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = Ui.Dim, Font = Ui.Body, Margin = new Padding(0, 8, 8, 0) };

    BindingAction? BuildAction()
    {
        switch (_kind)
        {
            case Kind.Default: return null;
            case Kind.Disabled: return new Disabled();
            case Kind.Key:
                var mods = (_ctrl.Checked ? KeyModifiers.LeftCtrl : 0) | (_shift.Checked ? KeyModifiers.LeftShift : 0)
                         | (_alt.Checked ? KeyModifiers.LeftAlt : 0) | (_win.Checked ? KeyModifiers.LeftWin : 0);
                byte usage = Selected<byte>(_usage);
                return mods == KeyModifiers.None && HidUsage.IsFKey(usage) ? new FKey(usage) : new StandardKey(mods, usage);
            case Kind.FKey: return new FKey(Selected<byte>(_fkey));
            case Kind.Media: return new Media(Selected<MediaAction>(_media));
            case Kind.Mouse: return new MouseButton(Selected<MouseButtonKind>(_mouse), _hold.Checked, _double.Checked, (byte)_autoFire.Value);
            case Kind.Scroll: return new MouseScroll(Selected<MouseScrollDirection>(_scroll));
            case Kind.WindowsShortcut: return new WindowsShortcut(Selected<WindowsShortcutAction>(_shortcut));
            case Kind.Backlight:
                var a = Selected<BacklightAction>(_backlight);
                return new Backlight(a, a == BacklightAction.SelectEffect ? Selected<Effect>(_effect) : null);
            case Kind.Character:
                var text = _text.Text.Trim();
                if (text.Length == 0) throw new ArgumentException("Type the character first.");
                int cp = char.ConvertToUtf32(text, 0);
                if (cp > 0xFFFF) throw new ArgumentException("That character is not supported by the keyboard.");
                return new AltCode((ushort)cp);
            case Kind.Website:
                var url = _text.Text.Trim();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    throw new ArgumentException("Enter a full web address starting with https://");
                return new OpenBrowser(url);
        }
        return null;
    }

    // ---------------------------------------------------------------- applying

    async Task ApplyKey()
    {
        if (SelectedKey is not { } id) { _status.Text = "Click a key first."; return; }
        var layer = CurrentLayer;
        BindingAction? action;
        try { action = BuildAction(); }
        catch (ArgumentException e) { _status.Text = e.Message; return; }

        _status.Text = "Applying…";
        try
        {
            await _keyboard.Run(q =>
            {
                KeyboardBackupGuard.EnsureBackup(q);
                var b = new Bindings(q);
                if (action is null) b.Clear(id, layer);
                else b.SetBinding(new KeyBinding(id, layer, action));
            });
            _bindings.RemoveAll(x => x.KeyId == id && x.Layer == layer);
            if (action is not null) _bindings.Add(new KeyBinding(id, layer, action));
            RefreshMarks();
            ShowSelected();
            _status.Text = $"✔ {KeyIds.Get(id).Label}: {(action is null ? "back to its normal function" : Describe(action))}.";
        }
        catch (Exception e) { _status.Text = Friendly(e); }
    }

    async Task ApplyEnabled()
    {
        bool on = _enabled.Checked;
        try
        {
            await _keyboard.Run(q => { KeyboardBackupGuard.EnsureBackup(q); new Bindings(q).SetEnabled(on); });
            _status.Text = on ? "Custom key bindings are on." : "Custom key bindings are off (keys use their normal functions).";
        }
        catch (Exception e) { _status.Text = Friendly(e); _enabled.Checked = !on; }
    }

    async Task ApplyGameMode()
    {
        var locks = (_lockWin.Checked ? GameModeLocks.Win : 0) | (_lockAltTab.Checked ? GameModeLocks.AltTab : 0)
                  | (_lockAltF4.Checked ? GameModeLocks.AltF4 : 0) | (_lockShiftTab.Checked ? GameModeLocks.ShiftTab : 0)
                  | (_lockCaps.Checked ? GameModeLocks.CapsLock : 0);
        bool on = _gameMode.Checked;
        try
        {
            await _keyboard.Run(q =>
            {
                KeyboardBackupGuard.EnsureBackup(q);
                var s = new KeyboardSettings(q);
                s.SetLocks(locks);
                s.SetGameMode(on);
            });
            _status.Text = $"Game Mode {(on ? "ON" : "off")}; blocked while on: {(locks == 0 ? "nothing" : locks.ToString())}.";
        }
        catch (Exception e) { _status.Text = Friendly(e); }
    }

    async Task RestoreFactory()
    {
        if (MessageBox.Show(this, "Remove all custom key bindings and restore the be quiet! factory bindings?\n" +
                "(Your original bindings are backed up and can be restored from the Profiles page.)", "OverMount",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _status.Text = "Restoring factory bindings…";
        try
        {
            _bindings = await _keyboard.Run(q =>
            {
                KeyboardBackupGuard.EnsureBackup(q);
                var b = new Bindings(q);
                foreach (var existing in b.GetAll()) b.Clear(existing.KeyId, existing.Layer);
                foreach (var d in BindingDefaults.Factory) b.SetBinding(d);
                return b.GetAll().ToList();
            });
            RefreshMarks();
            ShowSelected();
            _status.Text = "Factory bindings restored.";
        }
        catch (Exception e) { _status.Text = Friendly(e); }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Compact key-cap text for the keyboard picture.</summary>
    /// <summary>A keycap-sized label for a key id (shared with the Lighting studio).</summary>
    internal static string ShortLabel(byte id)
    {
        if (id is >= KeyIds.DisplayKey1 and <= KeyIds.DisplayKey8) return $"B{id - KeyIds.DisplayKey1 + 1}";
        var label = KeyIds.Find(id)?.Label ?? "?";
        if (label.StartsWith("Numpad ", StringComparison.OrdinalIgnoreCase)) label = label[7..];
        if (label.StartsWith("Prev", StringComparison.OrdinalIgnoreCase)) return "Prev";
        if (label.StartsWith("Next", StringComparison.OrdinalIgnoreCase)) return "Next";
        if (label.StartsWith("Play", StringComparison.OrdinalIgnoreCase)) return "Play";
        return label switch
        {
            "Print Screen" or "PrintScreen" => "PrtSc", "Scroll Lock" or "ScrollLock" => "ScrLk", "Pause" or "Pause/Break" => "Pause",
            "Insert" => "Ins", "Delete" => "Del", "Page Up" or "PageUp" => "PgUp", "Page Down" or "PageDown" => "PgDn",
            "Escape" => "Esc", "Backspace" => "⌫", "Caps Lock" => "Caps", "Left Shift" or "Right Shift" => "Shift",
            "Left Ctrl" or "Right Ctrl" => "Ctrl", "Left Alt" => "Alt", "Left WIN" or "Right Win" or "Right WIN" => "Win",
            "Num Lock" or "NumLock" => "Num", "Play/Pause" => "Play", "Previous" => "Prev", "Enter" => "Enter",
            _ => label.Length > 6 ? label[..6] : label,
        };
    }

    /// <summary>Very short text for a keycap's second line.</summary>
    static string Short(BindingAction action) => action switch
    {
        Disabled => "OFF",
        StandardKey k => (k.Modifiers == KeyModifiers.None ? "" : ModifierText(k.Modifiers) + "+") + HidUsage.NameOf(k.Usage),
        FKey f => $"F{f.Number}",
        Media m => m.Action switch { MediaAction.VolumeUp => "Vol+", MediaAction.VolumeDown => "Vol−", MediaAction.PlayPause => "Play", MediaAction.NextTrack => "Next", MediaAction.PrevTrack => "Prev", _ => Words(m.Action.ToString()) },
        MouseButton b => b.Button switch { MouseButtonKind.Left => "LMB", MouseButtonKind.Right => "RMB", MouseButtonKind.Middle => "MMB", _ => "Mouse" },
        MouseScroll s => "Scroll",
        WindowsShortcut w => Words(w.Action.ToString()),
        Backlight => "Light",
        AltCode a => char.ConvertFromUtf32(a.CodePoint),
        OpenBrowser => "Web",
        _ => "App",
    };

    public static string Describe(BindingAction action) => action switch
    {
        Disabled => "disabled",
        StandardKey k => (k.Modifiers == KeyModifiers.None ? "" : ModifierText(k.Modifiers) + "+") + HidUsage.NameOf(k.Usage),
        FKey f => $"F{f.Number}",
        Media m => Words(m.Action.ToString()),
        MouseButton b => $"mouse {Words(b.Button.ToString()).ToLowerInvariant()}{(b.DoubleClick ? " double click" : "")}{(b.WhilePressed ? " (held)" : "")}{(b.AutoFire > 0 ? $", auto-fire {b.AutoFire}/s" : "")}",
        MouseScroll s => $"scroll {s.Direction.ToString().ToLowerInvariant()}",
        WindowsShortcut w => Words(w.Action.ToString()),
        Backlight b => Words(b.Action.ToString()) + (b.Effect is { } e ? $" ({e})" : ""),
        AltCode a => $"types {char.ConvertFromUtf32(a.CodePoint)}",
        OpenBrowser o => $"opens {o.Url}",
        OpenFile f => $"opens {f.Path}",
        OpenFolder f => $"opens folder {f.Path}",
        _ => "a desktop-app action (kept as is)",
    };

    static string ModifierText(KeyModifiers m) => string.Join("+", new[]
    {
        (m & (KeyModifiers.LeftCtrl | KeyModifiers.RightCtrl)) != 0 ? "Ctrl" : null,
        (m & (KeyModifiers.LeftShift | KeyModifiers.RightShift)) != 0 ? "Shift" : null,
        (m & (KeyModifiers.LeftAlt | KeyModifiers.RightAlt)) != 0 ? "Alt" : null,
        (m & (KeyModifiers.LeftWin | KeyModifiers.RightWin)) != 0 ? "Win" : null,
    }.Where(s => s is not null));

    static string Words(string pascal) =>
        System.Text.RegularExpressions.Regex.Replace(pascal, "(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");

    sealed record Item<T>(string Name, T Value) { public override string ToString() => Name; }

    static ComboBox Combo(int width) => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Font = Ui.Body };

    static T Selected<T>(ComboBox c) => c.SelectedItem is Item<T> i ? i.Value : default!;

    static void SelectValue<T>(ComboBox c, T value)
    {
        for (int i = 0; i < c.Items.Count; i++)
            if (c.Items[i] is Item<T> item && EqualityComparer<T>.Default.Equals(item.Value, value)) { c.SelectedIndex = i; return; }
    }

    static string Friendly(Exception e) => e is KeyboardUnavailableException ? e.Message : $"Something went wrong: {e.Message}";
}
