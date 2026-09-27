using System.Text;
using Darkmount.Keyboard;

namespace Darkmount.App.Pages;

/// <summary>Save, apply and auto-switch complete setups; restore the keyboard's original settings.</summary>
public sealed class ProfilesPage : Ui.Page
{
    readonly ProfileManager _manager;
    readonly Macros.MacroManager? _macros;
    readonly Func<string?> _currentGame;
    readonly ProfileSet _set;
    readonly ListBox _list = new() { Width = 300, Height = 200, Font = Ui.Body, BackColor = Ui.Panel, ForeColor = Ui.Text, BorderStyle = BorderStyle.None };
    readonly TextBox _games = new() { Width = 360, Font = Ui.Body };
    readonly ComboBox _default = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Font = Ui.Body };
    readonly CheckBox _withImages = Ui.Check("Include display-key images");
    readonly Label _status = Ui.Note("", 700);

    public ProfilesPage(ProfileManager manager, Func<string?> currentGame, Macros.MacroManager? macros = null)
        : base("Profiles", "A profile stores the keyboard's lighting, key bindings and Game Mode locks (optionally the " +
                           "display-key images) plus what the dock shows and the Lighting-studio scene. Profiles can switch " +
                           "automatically when a game starts. Coming from IO Center? Import your profiles in one click.")
    {
        _manager = manager;
        _macros = macros;
        _currentGame = currentGame;
        _set = new ProfileSet
        {
            DefaultProfile = manager.Set.DefaultProfile,
            Profiles = manager.Set.Profiles.Select(Clone).ToList(),
        };
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        _games.TextChanged += (_, _) =>
        {
            if (Selected is { } p) p.Games = _games.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        };

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        buttons.Controls.AddRange([
            Ui.Button("Save current setup as…", async (_, _) => await CaptureNew(), primary: true),
            Ui.Button("Apply selected", async (_, _) => await ApplySelected()),
            Ui.Button("Update from current setup", async (_, _) => await UpdateSelected()),
            Ui.Button("Delete", (_, _) => DeleteSelected()),
            Ui.Button("Import from IO Center…", async (_, _) => await ImportFromIoCenter()),
        ]);
        buttons.Controls.Add(_withImages);
        var top = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        top.Controls.AddRange([_list, buttons]);
        AddFull(top);

        var games = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        games.Controls.AddRange([_games, Ui.Button("Add the running game", (_, _) => AddRunningGame())]);
        Row("Use automatically for games", games, null);
        AddFull(Ui.Note("Game executables separated by commas, e.g. cs2.exe, eldenring.exe.", 640));
        Row("When the game closes, switch to", _default);
        _default.SelectedIndexChanged += (_, _) => _set.DefaultProfile = _default.SelectedIndex <= 0 ? null : (string)_default.SelectedItem!;

        var save = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        save.Controls.AddRange([Ui.Button("Save profiles", (_, _) => SaveAll(), primary: true),
            Ui.Button("Restore my original keyboard settings", async (_, _) => await RestoreOriginal())]);
        AddFull(save);
        AddFull(_status);
        RefreshList();
    }

    Profile? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _set.Profiles.Count ? _set.Profiles[_list.SelectedIndex] : null;

    void RefreshList()
    {
        int sel = _list.SelectedIndex;
        _list.Items.Clear();
        foreach (var p in _set.Profiles)
            _list.Items.Add(p.Name + (p.Games.Count > 0 ? $"  ({string.Join(", ", p.Games)})" : "") + (p.Name == _manager.ActiveProfile ? "  ✓" : ""));
        if (sel >= 0 && sel < _list.Items.Count) _list.SelectedIndex = sel;
        else if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        _default.Items.Clear();
        _default.Items.Add("(keep the current settings)");
        foreach (var p in _set.Profiles) _default.Items.Add(p.Name);
        _default.SelectedIndex = Math.Max(0, _set.Profiles.FindIndex(p => p.Name == _set.DefaultProfile) + 1);
        ShowSelected();
    }

    void ShowSelected() => _games.Text = Selected is { } p ? string.Join(", ", p.Games) : "";

    async Task CaptureNew()
    {
        var name = Prompt("Profile name", $"Profile {_set.Profiles.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        _status.Text = "Reading the current setup from the keyboard…";
        try
        {
            var p = await _manager.Capture(name.Trim(), _withImages.Checked);
            _set.Profiles.RemoveAll(x => x.Name == p.Name);
            _set.Profiles.Add(p);
            RefreshList();
            _list.SelectedIndex = _set.Profiles.Count - 1;
            SaveAll();
            _status.Text = $"Saved \"{p.Name}\".";
        }
        catch (Exception e) { _status.Text = Friendly(e); }
    }

    async Task UpdateSelected()
    {
        if (Selected is not { } old) return;
        try
        {
            var p = await _manager.Capture(old.Name, _withImages.Checked);
            p.Games = old.Games;
            _set.Profiles[_list.SelectedIndex] = p;
            SaveAll();
            _status.Text = $"\"{p.Name}\" updated from the current setup.";
        }
        catch (Exception e) { _status.Text = Friendly(e); }
    }

    async Task ApplySelected()
    {
        if (Selected is not { } p) return;
        _status.Text = $"Applying \"{p.Name}\"…";
        try
        {
            int writes = await _manager.Apply(p);
            RefreshList();
            _status.Text = $"\"{p.Name}\" applied ({writes} change(s) written to the keyboard).";
        }
        catch (Exception e) { _status.Text = Friendly(e); }
    }

    async Task ImportFromIoCenter()
    {
        if (IoCenterImportDialog.Ask(FindForm()) is not { Count: > 0 } paths) return;
        _status.Text = "Importing…";
        var macroList = _macros?.Macros.Select(m => m.Clone()).ToList();
        int macrosBefore = macroList?.Count ?? 0;
        var report = new StringBuilder();
        int imported = 0;
        foreach (var path in paths)
        {
            try
            {
                var result = await _manager.ImportIoCenter(path, macroList);
                var p = result.Profile;
                p.Name = UniqueName(p.Name);
                _set.Profiles.Add(p);
                imported++;
                report.AppendLine($"✔  {p.Name}");
                foreach (var note in result.Notes) report.AppendLine($"     •  {note}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                          or System.Text.Json.JsonException or InvalidOperationException)
            {
                report.AppendLine($"✖  {Path.GetFileName(path)}: {e.Message}");
            }
            report.AppendLine();
        }
        if (_macros is not null && macroList is not null && macroList.Count != macrosBefore) _macros.Save(macroList);
        RefreshList();
        if (imported > 0) _list.SelectedIndex = _set.Profiles.Count - 1;
        SaveAll();
        _status.Text = imported == 0 ? "Nothing was imported." :
            $"{imported} profile(s) imported. Select one and press \"Apply selected\" to use it.";
        ShowReport(report.ToString().TrimEnd());
    }

    string UniqueName(string name)
    {
        if (_set.Profiles.All(p => p.Name != name)) return name;
        for (int i = 1; ; i++)
        {
            var candidate = i == 1 ? $"{name} (IO Center)" : $"{name} (IO Center {i})";
            if (_set.Profiles.All(p => p.Name != candidate)) return candidate;
        }
    }

    void ShowReport(string text)
    {
        using var f = new Form
        {
            Text = "IO Center import", Width = 760, Height = 520, StartPosition = FormStartPosition.CenterParent, BackColor = Ui.Back,
            ForeColor = Ui.Text, Font = Ui.Body, MinimizeBox = false, MaximizeBox = false,
        };
        Ui.BeginLayout(f);
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = text,
            BackColor = Ui.Panel, ForeColor = Ui.Text, BorderStyle = BorderStyle.None, Font = Ui.Body,
        };
        var ok = Ui.Button("OK", (_, _) => f.Close(), primary: true);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        bar.Controls.Add(ok);
        f.Controls.Add(box);
        f.Controls.Add(bar);
        f.AcceptButton = ok;
        Ui.EndLayout(f);
        f.ShowDialog(FindForm());
    }

    void DeleteSelected()
    {
        if (Selected is null) return;
        _set.Profiles.RemoveAt(_list.SelectedIndex);
        RefreshList();
        SaveAll();
    }

    void AddRunningGame()
    {
        if (Selected is not { } p) { _status.Text = "Select a profile first."; return; }
        if (_currentGame() is not { } game) { _status.Text = "No game is running right now (RivaTuner must see it)."; return; }
        if (!p.Games.Contains(game, StringComparer.OrdinalIgnoreCase)) p.Games.Add(game);
        ShowSelected();
        RefreshList();
    }

    void SaveAll()
    {
        _manager.Save(new ProfileSet { DefaultProfile = _set.DefaultProfile, Profiles = _set.Profiles.Select(Clone).ToList() });
        _status.Text = "Profiles saved.";
    }

    async Task RestoreOriginal()
    {
        if (KeyboardBackupGuard.Original is not { } original)
        {
            _status.Text = "There is no backup yet — nothing has been changed on the keyboard by OverMount.";
            return;
        }
        if (MessageBox.Show(this, "Write your original keyboard settings (lighting, key bindings, Game Mode locks and display-key " +
                "images, as they were before OverMount changed anything) back to the keyboard?", "OverMount",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _status.Text = "Restoring…";
        try
        {
            int writes = await _manager.Apply(new Profile { Name = "Original", Keyboard = original, Mode = ScreenMode.Auto });
            _status.Text = $"Original keyboard settings restored ({writes} change(s)).";
        }
        catch (Exception e) { _status.Text = Friendly(e); }
    }

    static Profile Clone(Profile p) =>
        System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(p))!;

    string? Prompt(string label, string initial) => Ui.Prompt(FindForm(), "OverMount", label, initial);

    static string Friendly(Exception e) => e is KeyboardUnavailableException ? e.Message : $"Something went wrong: {e.Message}";
}
