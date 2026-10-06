using Darkmount.App.Macros;
using Darkmount.Keyboard;
using MacroMouseButton = Darkmount.App.Macros.MouseButton;

namespace Darkmount.App.Pages;

/// <summary>
/// Macros that run on the PC: keystrokes, text, delays, mouse, media keys, apps, websites and folders. A macro is
/// triggered by a key combination pressed on any keyboard (e.g. Ctrl+Shift+K), or by F13–F24 (optionally with
/// Ctrl/Shift/Alt/Win), which a Dark Mount key can be bound to send here.
/// </summary>
public sealed class MacrosPage : Ui.Page
{
    readonly MacroManager _manager;
    readonly bool _keyBinding;
    readonly KeyboardService _keyboard;
    readonly List<Macro> _macros;
    readonly ListBox _list = new() { Width = 260, Height = 250, Font = Ui.Body, BackColor = Ui.Panel, ForeColor = Ui.Text, BorderStyle = BorderStyle.None };
    readonly ListBox _steps = new() { Width = 420, Height = 250, Font = Ui.Body, BackColor = Ui.Panel, ForeColor = Ui.Text, BorderStyle = BorderStyle.None };
    readonly TextBox _name = new() { Width = 260, Font = Ui.Body };
    readonly ComboBox _trigger = TriggerCombo(), _mode = Ui.Combo<PlaybackMode>(150);
    readonly CheckBox _ctrl = Ui.Check("Ctrl"), _shift = Ui.Check("Shift"), _alt = Ui.Check("Alt"), _win = Ui.Check("Win"), _enabled = Ui.Check("Enabled");
    readonly Label _triggerHelp = Ui.Note("", 440);
    readonly FlowLayoutPanel _bind = new() { AutoSize = true, WrapContents = false };
    readonly Label _anyKeyboard = Ui.Note("Press this key combination on any keyboard to run the macro.", 720);
    readonly NumericUpDown _repeat = Ui.Number(1, 10000);
    readonly ComboBox _bindKey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Font = Ui.Body };
    readonly Button _record;
    readonly Label _status = Ui.Note("", 720);
    MacroRecorder? _recorder;
    Macro? _recordingInto;
    bool _loading;

    /// <param name="keyBinding">False without a be quiet! keyboard: no "bind a Dark Mount key" row.</param>
    public MacrosPage(MacroManager manager, KeyboardService keyboard, bool keyBinding = true)
        : base("Macros", "Macros run on your PC while OverMount is running. Each macro has a trigger: a key combination " +
                         "such as Ctrl+Shift+K that works on any keyboard, or F13–F24 (which normal keyboards don't have) — bind " +
                         "any key of your Dark Mount, e.g. a display key, to one of those below. Keys other than F13–F24 need " +
                         "Ctrl, Alt, Shift or Win, so normal typing is never taken over.")
    {
        _manager = manager;
        _keyBinding = keyBinding;
        _keyboard = keyboard;
        _macros = manager.Macros.Select(m => m.Clone()).ToList();
        _record = Ui.Button("● Record", (_, _) => ToggleRecord());

        _list.SelectedIndexChanged += (_, _) => ShowMacro();
        foreach (var k in KeyIds.All.Where(k => KeyIds.IsRebindable(k.Id, Layer.Common)))
            _bindKey.Items.Add(new KeyItem(k.Id, k.Zone == KeyZone.DisplayKey ? $"Display key B{k.Id - KeyIds.DisplayKey1 + 1}" : k.Zone == KeyZone.DockButton ? $"Dock: {k.Label}" : k.Label));
        _bindKey.SelectedIndex = _bindKey.Items.Cast<KeyItem>().ToList().FindIndex(k => k.Id == KeyIds.DisplayKey1);
        foreach (var c in new Control[] { _name, _trigger, _mode, _ctrl, _shift, _alt, _win, _enabled, _repeat })
        {
            if (c is TextBox t) t.TextChanged += (_, _) => Commit();
            else if (c is ComboBox cb) cb.SelectedIndexChanged += (_, _) => Commit();
            else if (c is CheckBox ch) ch.CheckedChanged += (_, _) => Commit();
            else if (c is NumericUpDown n) n.ValueChanged += (_, _) => Commit();
        }

        var listButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        listButtons.Controls.AddRange([Ui.Button("New", (_, _) => NewMacro()), Ui.Button("Delete", (_, _) => DeleteMacro()),
            Ui.Button("Add examples", (_, _) => AddExamples())]);
        var left = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        left.Controls.AddRange([_list, listButtons]);

        var trigger = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        trigger.Controls.AddRange([_ctrl, _shift, _alt, _win, _trigger]);
        var mode = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        mode.Controls.AddRange([_mode, Hint("times"), _repeat, _enabled]);
        var stepButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(440, 0) };
        stepButtons.Controls.AddRange([_record, Ui.Button("Add step…", (s, _) => AddStepMenu((Control)s!)),
            Ui.Button("Remove", (_, _) => RemoveStep()), Ui.Button("▲", (_, _) => MoveStep(-1)), Ui.Button("▼", (_, _) => MoveStep(1)),
            Ui.Button("▶ Test", (_, _) => TestMacro())]);
        var right = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
        void R(string label, Control c) { right.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Ui.Text, Margin = new Padding(0, 9, 10, 0) }); right.Controls.Add(c); }
        R("Name", _name);
        R("Trigger", trigger);
        R("", _triggerHelp);
        R("Plays", mode);
        R("Steps", _steps);
        R("", stepButtons);

        var editor = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        editor.Controls.AddRange([left, right]);
        AddFull(editor);

        // Only F13–F24 triggers can be bound to a Dark Mount key; other combinations show _anyKeyboard instead.
        _bind.Controls.AddRange([Hint("Dark Mount key that triggers this macro:"), _bindKey, Ui.Button("Bind key", async (_, _) => await BindKey())]);
        AddFull(_bind);
        AddFull(_anyKeyboard);
        var save = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        save.Controls.AddRange([Ui.Button("Save macros", (_, _) => SaveAll(), primary: true), Ui.Button("Stop all running macros", (_, _) => _manager.StopAll())]);
        AddFull(save);
        AddFull(_status);
        AddFull(Ui.Note("Windows won't let macros type into apps running as administrator unless OverMount also runs as " +
                        "administrator. Games with anti-cheat may ignore simulated input.", 720));

        foreach (var c in new ComboBox[] { _trigger, _mode }) c.SelectedIndex = 0;
        RefreshList();
        if (_macros.Count > 0) _list.SelectedIndex = 0; else ShowMacro();
    }

    Macro? Current => _list.SelectedIndex >= 0 && _list.SelectedIndex < _macros.Count ? _macros[_list.SelectedIndex] : null;

    void RefreshList()
    {
        int sel = _list.SelectedIndex;
        _list.Items.Clear();
        foreach (var m in _macros) _list.Items.Add($"{(m.Enabled ? "" : "(off) ")}{m.Name}  —  {m.Trigger}");
        if (sel >= 0 && sel < _list.Items.Count) _list.SelectedIndex = sel;
    }

    void ShowMacro()
    {
        var m = Current;
        _loading = true;
        foreach (var c in new Control[] { _name, _trigger, _mode, _ctrl, _shift, _alt, _win, _enabled, _repeat, _steps }) c.Enabled = m is not null;
        if (m is not null)
        {
            _name.Text = m.Name;
            _trigger.SelectedItem = m.Trigger.Key;
            (_ctrl.Checked, _shift.Checked, _alt.Checked, _win.Checked) = (m.Trigger.Ctrl, m.Trigger.Shift, m.Trigger.Alt, m.Trigger.Win);
            _mode.SelectedItem = m.Mode;
            _repeat.Value = Math.Clamp(m.RepeatCount, 1, 10000);
            _enabled.Checked = m.Enabled;
        }
        _loading = false;
        ShowTriggerHelp();
        ShowSteps();
    }

    /// <summary>Explains the current trigger, and offers key binding only for F13–F24 (the keys a Dark Mount can send).</summary>
    void ShowTriggerHelp()
    {
        var t = Current?.Trigger;
        _triggerHelp.ForeColor = t is { IsValid: false } ? Ui.Accent : Ui.Dim;
        _triggerHelp.Text = t switch
        {
            null => "",
            { IsValid: false } => t.Problem!,
            { UsesF13ToF24: true } => "Normal keyboards don't have F13–F24: bind a Dark Mount key to this trigger below.",
            _ => "Works on any keyboard. While the macro is on, this combination runs it in every app instead of doing " +
                 "what it normally does, so pick one you don't use.",
        };
        _bind.Visible = _keyBinding && (t is null || t.UsesF13ToF24);
        _anyKeyboard.Visible = !_bind.Visible;
    }

    void ShowSteps()
    {
        int sel = _steps.SelectedIndex;
        _steps.Items.Clear();
        foreach (var s in Current?.Steps ?? []) _steps.Items.Add(s.Describe());
        if (sel >= 0 && sel < _steps.Items.Count) _steps.SelectedIndex = sel;
    }

    void Commit()
    {
        if (_loading || Current is not { } m) return;
        m.Name = _name.Text;
        m.Trigger = new MacroTrigger((TriggerKey)(_trigger.SelectedItem ?? TriggerKey.F13), _ctrl.Checked, _shift.Checked, _alt.Checked, _win.Checked);
        m.Mode = (PlaybackMode)(_mode.SelectedItem ?? PlaybackMode.Once);
        m.RepeatCount = (int)_repeat.Value;
        m.Enabled = _enabled.Checked;
        _repeat.Enabled = m.Mode == PlaybackMode.RepeatCount;
        ShowTriggerHelp();
        RefreshList();
    }

    void NewMacro()
    {
        if (_recorder is not null) return;
        var used = MacroManager.FreeTrigger(_macros);
        _macros.Add(new Macro { Name = $"Macro {_macros.Count + 1}", Trigger = used });
        RefreshList();
        _list.SelectedIndex = _macros.Count - 1;
    }

    void DeleteMacro()
    {
        if (Current is null || _recorder is not null) return;
        _macros.RemoveAt(_list.SelectedIndex);
        RefreshList();
        ShowMacro();
    }

    void AddExamples()
    {
        foreach (var e in MacroLibrary.Examples())
            if (_macros.All(m => m.Name != e.Name)) _macros.Add(e);
        RefreshList();
        _status.Text = "Examples added (disabled). Enable one, bind a key and save to try it.";
    }

    // ---------------------------------------------------------------- steps

    void AddStepMenu(Control anchor)
    {
        if (Current is null) { _status.Text = "Create a macro first."; return; }
        var menu = new ContextMenuStrip();
        menu.Items.Add("Press a key or shortcut…", null, (_, _) => AddStep(KeyCaptureDialog.Ask(FindForm())));
        menu.Items.Add("Type text…", null, (_, _) => AddStep(Prompt("Text to type") is { Length: > 0 } t ? new TypeTextStep(t) : null));
        menu.Items.Add("Wait…", null, (_, _) => AddStep(int.TryParse(Prompt("Milliseconds to wait", "500"), out int ms) && ms > 0 ? new DelayStep(Math.Min(ms, 600000)) : null));
        menu.Items.Add("Mouse click (left)", null, (_, _) => AddStep(new MouseClickStep(MacroMouseButton.Left)));
        menu.Items.Add("Mouse click (right)", null, (_, _) => AddStep(new MouseClickStep(MacroMouseButton.Right)));
        var media = new ToolStripMenuItem("Media key");
        foreach (var k in Enum.GetValues<MediaKey>()) media.DropDownItems.Add(k.ToString(), null, (_, _) => AddStep(new MediaKeyStep(k)));
        menu.Items.Add(media);
        menu.Items.Add("Launch a program…", null, (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "Programs|*.exe;*.bat;*.cmd;*.lnk|All files|*.*" };
            AddStep(dlg.ShowDialog(FindForm()) == DialogResult.OK ? new LaunchProgramStep(dlg.FileName) : null);
        });
        menu.Items.Add("Open a website…", null, (_, _) => AddStep(Prompt("Web address", "https://") is { Length: > 8 } u ? new OpenUrlStep(u) : null));
        menu.Items.Add("Open a folder…", null, (_, _) =>
        {
            using var dlg = new FolderBrowserDialog();
            AddStep(dlg.ShowDialog(FindForm()) == DialogResult.OK ? new OpenFolderStep(dlg.SelectedPath) : null);
        });
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    void AddStep(MacroStep? step)
    {
        if (step is null || Current is null) return;
        int at = _steps.SelectedIndex >= 0 ? _steps.SelectedIndex + 1 : Current.Steps.Count;
        Current.Steps.Insert(at, step);
        ShowSteps();
        _steps.SelectedIndex = at;
    }

    void RemoveStep()
    {
        if (Current is null || _steps.SelectedIndex < 0) return;
        Current.Steps.RemoveAt(_steps.SelectedIndex);
        ShowSteps();
    }

    void MoveStep(int delta)
    {
        if (Current is null || _steps.SelectedIndex < 0) return;
        int i = _steps.SelectedIndex, j = i + delta;
        if (j < 0 || j >= Current.Steps.Count) return;
        (Current.Steps[i], Current.Steps[j]) = (Current.Steps[j], Current.Steps[i]);
        ShowSteps();
        _steps.SelectedIndex = j;
    }

    void ToggleRecord()
    {
        if (Current is null) { _status.Text = "Create a macro first."; return; }
        if (_recorder is null)
        {
            _manager.SuspendTriggers();
            _recordingInto = Current;
            _list.Enabled = false; // the steps go to the macro recording started on
            _recorder = new MacroRecorder();
            _recorder.Start();
            _record.Text = "■ Stop recording";
            _status.Text = "Recording your keystrokes… press \"Stop recording\" when done.";
            return;
        }
        var steps = _recorder.Stop();
        _recorder.Dispose();
        _recorder = null;
        _manager.ResumeTriggers();
        _record.Text = "● Record";
        _list.Enabled = true;
        var target = _recordingInto;
        _recordingInto = null;
        if (target is null || !_macros.Contains(target)) { _status.Text = "The macro was removed while recording."; return; }
        // The final click on "Stop recording" is not a keystroke, so nothing needs trimming.
        target.Steps.AddRange(steps);
        ShowSteps();
        _status.Text = $"Recorded {steps.Count} step(s).";
    }

    void TestMacro()
    {
        if (Current is not { } m || m.Steps.Count == 0) { _status.Text = "Add some steps first."; return; }
        _status.Text = $"Playing \"{m.Name}\" in 2 seconds — click into the window where it should type.";
        var copy = m.Clone();
        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) => { timer.Dispose(); _manager.Test(copy); };
        timer.Start();
    }

    void SaveAll()
    {
        var failures = _manager.Save(_macros);
        _status.Text = failures.Count == 0
            ? $"Saved {_macros.Count} macro(s); triggers are active."
            : "Saved, but these triggers could not be registered: " + string.Join("; ", failures.Select(f => $"{f.Macro.Name} ({f.Reason})"));
    }

    async Task BindKey()
    {
        if (Current is not { } m) return;
        if (_bindKey.SelectedItem is not KeyItem key || !m.Trigger.UsesF13ToF24) return;
        var action = IoCenter.IoCenterMacros.TriggerAction(m.Trigger);
        _status.Text = "Binding…";
        try
        {
            await _keyboard.Run(q =>
            {
                KeyboardBackupGuard.EnsureBackup(q);
                new Bindings(q).SetBinding(new KeyBinding(key.Id, Layer.Common, action));
            });
            SaveAll();
            _status.Text = $"{key.Name} now triggers \"{m.Name}\" ({m.Trigger}). Macros are saved and active.";
        }
        catch (Exception e) { _status.Text = e is KeyboardUnavailableException ? e.Message : $"Something went wrong: {e.Message}"; }
    }

    /// <summary>Trigger keys in <see cref="MacroTrigger.AllKeys"/> order, shown as "K", "F5", "Num 1", "Page Up", …</summary>
    static ComboBox TriggerCombo()
    {
        var c = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, Font = Ui.Body, FlatStyle = FlatStyle.Flat,
            FormattingEnabled = true, MaxDropDownItems = 20,
        };
        foreach (var k in MacroTrigger.AllKeys) c.Items.Add(k);
        c.Format += (_, e) => { if (e.ListItem is TriggerKey k) e.Value = MacroTrigger.KeyName(k); };
        return c;
    }

    static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = Ui.Dim, Margin = new Padding(6, 9, 6, 0) };

    string? Prompt(string label, string initial = "") => Ui.Prompt(FindForm(), "OverMount", label, initial);

    sealed record KeyItem(byte Id, string Name) { public override string ToString() => Name; }
}

/// <summary>Captures one key or shortcut the user presses.</summary>
sealed class KeyCaptureDialog : Form
{
    KeyTapStep? _result;

    KeyCaptureDialog()
    {
        Ui.BeginLayout(this);
        Text = "Press a key or shortcut";
        Width = 420;
        Height = 150;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        BackColor = Ui.Back;
        ForeColor = Ui.Text;
        Controls.Add(new Label
        {
            Text = "Press the key or combination (e.g. Ctrl+Shift+S).\nEsc cancels.", AutoSize = true, Left = 18, Top = 20, Font = Ui.Body,
        });
        Ui.EndLayout(this);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (key == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return true; // wait for the main key
        var mods = ((keyData & Keys.Control) != 0 ? MacroModifiers.Ctrl : 0) | ((keyData & Keys.Shift) != 0 ? MacroModifiers.Shift : 0)
                 | ((keyData & Keys.Alt) != 0 ? MacroModifiers.Alt : 0);
        _result = new KeyTapStep((int)key, mods);
        DialogResult = DialogResult.OK;
        return true;
    }

    public static KeyTapStep? Ask(IWin32Window? owner)
    {
        using var d = new KeyCaptureDialog();
        return d.ShowDialog(owner) == DialogResult.OK ? d._result : null;
    }
}
