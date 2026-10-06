using System.Diagnostics;

namespace Darkmount.App.Pages;

/// <summary>One line of the setup checklist.</summary>
public sealed record SetupCheck(string Title, bool Ok, bool Optional, string Detail, string? FixLabel = null, Action? Fix = null);

/// <summary>Everything the Home page shows, gathered by the tray app once per second.</summary>
public sealed record HomeStatus(
    string Keyboard, string Dock, string Lighting, string Profile, string Macros, IReadOnlyList<SetupCheck> Checks);

/// <summary>
/// The first page: what's connected and happening, big one-click actions, and a setup checklist that explains
/// (and where possible fixes) anything that stops a feature from working.
/// </summary>
public sealed class HomePage : Ui.Page
{
    readonly Func<HomeStatus> _status;
    readonly Label _keyboard = Card(), _dock = Card(), _lighting = Card(), _profile = Card(), _macros = Card();
    readonly TableLayoutPanel _checks = new() { AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 4, 0, 4) };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    string _lastChecks = "", _lastError = "";

    /// <param name="hasDock">False on Light Mount keyboards: the dock tile, dock actions and dock tips are left out.</param>
    /// <param name="beQuiet">False on other makers' keyboards: be quiet!-only actions and tips are left out too.</param>
    public HomePage(Func<HomeStatus> status, Action<ScreenMode> setMode, Action<string> openPage, Action togglePause, bool hasDock = true,
        bool beQuiet = true)
        : base("Welcome to OverMount", "Your keyboard, your way. Everything below updates live.")
    {
        _status = status;
        var cards = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(820, 0) };
        cards.Controls.Add(Tile("KEYBOARD", _keyboard));
        if (hasDock) cards.Controls.Add(Tile("DOCK SCREEN", _dock));
        cards.Controls.AddRange([Tile("LIGHTING", _lighting), Tile("PROFILE", _profile), Tile("MACROS", _macros)]);
        AddFull(cards);

        Heading("Quick actions");
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(820, 0) };
        if (hasDock)
            actions.Controls.AddRange([
                Big("Show stats", () => setMode(ScreenMode.Auto)),
                Big("Show animation", () => setMode(ScreenMode.Animation)),
                Big("be quiet! screen", () => setMode(ScreenMode.DockDefault)),
            ]);
        actions.Controls.Add(Big("Lighting", () => openPage("Lighting")));
        if (beQuiet) actions.Controls.Add(Big("Remap keys", () => openPage("Keys")));
        actions.Controls.Add(Big("Macros", () => openPage("Macros")));
        if (hasDock) actions.Controls.Add(Big("Pause / resume", togglePause));
        AddFull(actions);

        Heading("Setup check");
        _checks.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        _checks.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _checks.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddFull(_checks);

        Heading("Tips");
        AddFull(Ui.Note(beQuiet
            ? "• Ctrl+Alt+Shift+D cycles the dock: dashboard → animation → be quiet! screen.\n" +
              "• If the dock screen is dark, press a dock button once — it only accepts pictures while awake.\n" +
              "• Coming from IO Center? Profiles → Import from IO Center brings your lighting, keys and display-key pictures over.\n" +
              "• Your original keyboard settings are backed up before the first change: Profiles → Restore.\n" +
              "• Close OverMount from the tray icon to hand everything back to the keyboard."
            : "• Lighting → Your RGB devices shows every keyboard OverMount lights; untick one to leave it alone.\n" +
              "• A laptop or keyboard missing there? Lighting → Set up OpenRGB lights hundreds more.\n" +
              "• Macros run on any keyboard: give one a combination such as Ctrl+Shift+K.\n" +
              "• Close OverMount from the tray icon to give the lighting back to your keyboard.", 760));

        _timer.Tick += (_, _) => ShowStatus();
        VisibleChanged += (_, _) => { if (Visible) { ShowStatus(); _timer.Start(); } else _timer.Stop(); };
        Disposed += (_, _) => _timer.Dispose();
    }

    void ShowStatus()
    {
        HomeStatus s;
        try { s = _status(); }
        catch (Exception e) // a status read failing must never break the page (it runs inside page switches)
        {
            if (e.Message != _lastError) Log.Write($"Home status failed: {e.Message}"); // refreshes every second: log once
            _lastError = e.Message;
            return;
        }
        _keyboard.Text = s.Keyboard;
        _dock.Text = s.Dock;
        _lighting.Text = s.Lighting;
        _profile.Text = s.Profile;
        _macros.Text = s.Macros;

        string key = string.Join("|", s.Checks.Select(c => $"{c.Title}{c.Ok}{c.Detail}"));
        if (key == _lastChecks) return;
        _lastChecks = key;
        _checks.SuspendLayout();
        var old = _checks.Controls.Cast<Control>().ToList();
        _checks.Controls.Clear();
        foreach (var o in old) o.Dispose();
        foreach (var c in s.Checks)
        {
            Ui.Add(_checks, new Label
            {
                Text = c.Ok ? "✔" : c.Optional ? "○" : "⚠", AutoSize = true, Font = new Font("Segoe UI Semibold", 12f),
                ForeColor = c.Ok ? Color.FromArgb(76, 217, 100) : c.Optional ? Ui.Dim : Color.FromArgb(255, 176, 32), Margin = new Padding(0, 6, 0, 0),
            });
            var text = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 4, 12, 4) };
            text.Controls.Add(new Label { Text = c.Title, AutoSize = true, Font = Ui.Body, ForeColor = Ui.Text, Margin = new Padding(0) });
            text.Controls.Add(Ui.Note(c.Detail, 560));
            Ui.Add(_checks, text);
            Ui.Add(_checks, c.Fix is { } fix && !c.Ok ? Ui.Button(c.FixLabel ?? "Fix", (_, _) => fix()) : new Label { AutoSize = true });
        }
        _checks.ResumeLayout();
    }

    static Label Card() => new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 11.5f), ForeColor = Ui.Text, MaximumSize = new Size(230, 0), Margin = new Padding(0, 2, 0, 0) };

    static Panel Tile(string caption, Label value)
    {
        // Grows taller when the text needs it (large Windows scaling makes the text bigger than the tile).
        var p = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(250, 78), MaximumSize = new Size(250, 0), BackColor = Ui.Panel, Padding = new Padding(12, 10, 10, 8),
            Margin = new Padding(0, 0, 12, 12),
        };
        p.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Ui.Accent, Font = new Font("Segoe UI Semibold", 8.5f) });
        p.Controls.Add(value);
        return p;
    }

    static Button Big(string text, Action onClick)
    {
        var b = Ui.Button(text, (_, _) => onClick());
        b.MinimumSize = new Size(190, 46);
        b.Font = new Font("Segoe UI Semibold", 10.5f);
        b.TextAlign = ContentAlignment.MiddleCenter;
        b.Margin = new Padding(0, 0, 10, 10);
        return b;
    }

    /// <summary>Opens a URL or ms-settings: page.</summary>
    public static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
