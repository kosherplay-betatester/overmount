namespace Darkmount.App;

/// <summary>Small helpers so every settings page has the same dark, aligned look.</summary>
public static class Ui
{
    public static readonly Color Back = Color.FromArgb(18, 20, 24);
    public static readonly Color Panel = Color.FromArgb(28, 31, 37);
    public static readonly Color PanelHover = Color.FromArgb(38, 42, 50);
    public static readonly Color Text = Color.FromArgb(232, 234, 238);
    public static readonly Color Dim = Color.FromArgb(150, 156, 168);
    public static readonly Color Accent = Color.FromArgb(255, 138, 31);

    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font Title = new("Segoe UI Semibold", 15f);
    public static readonly Font Section = new("Segoe UI Semibold", 11f);

    /// <summary>A page: title, optional description, then aligned label/control rows.</summary>
    public class Page : TableLayoutPanel
    {
        public Page(string title, string? description = null)
        {
            // Size to content (no stretched rows); the host panel scrolls.
            Dock = DockStyle.Top;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ColumnCount = 2;
            Padding = new Padding(24, 18, 24, 18);
            BackColor = Back;
            ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            // AutoSize, not Percent: when WinForms scales an auto-sized table to the screen, controls in a Percent column
            // collapse to zero width (text boxes and drop-downs simply vanish at 125 %+ Windows scaling).
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            AddFull(new Label { Text = title, Font = Title, ForeColor = Ui.Text, AutoSize = true, Margin = new Padding(0, 0, 0, 4), UseMnemonic = false });
            if (description is not null) AddFull(Note(description, 620));
            AddFull(new Label { Height = 8, AutoSize = false });
        }

        public void Row(string label, Control control, string? hint = null)
        {
            Controls.Add(new Label
            {
                Text = label, AutoSize = true, ForeColor = Ui.Text, Font = Body, Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 9, 12, 9), UseMnemonic = false,
            });
            control.Anchor = AnchorStyles.Left;
            control.Margin = new Padding(0, 6, 0, 6);
            if (hint is null)
            {
                Controls.Add(control);
                return;
            }
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
            flow.Controls.Add(control);
            flow.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Dim, Font = Body, Margin = new Padding(8, 9, 0, 0), UseMnemonic = false });
            Controls.Add(flow);
        }

        public void Row(Control left, Control right)
        {
            left.Anchor = AnchorStyles.Left;
            left.Margin = new Padding(0, 8, 12, 8);
            right.Anchor = AnchorStyles.Left;
            right.Margin = new Padding(0, 6, 0, 6);
            Controls.Add(left);
            Controls.Add(right);
        }

        public void Heading(string text) =>
            AddFull(new Label { Text = text, Font = Section, ForeColor = Accent, AutoSize = true, Margin = new Padding(0, 14, 0, 4), UseMnemonic = false });

        public void AddFull(Control c)
        {
            Controls.Add(c);
            SetColumnSpan(c, 2);
        }
    }

    public static Label Note(string text, int width = 520) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = Dim, Font = Body, Margin = new Padding(0, 2, 0, 6), UseMnemonic = false };

    /// <summary>A drop-down of enum values shown with friendly names (<see cref="Friendly"/>); items stay enum values.</summary>
    public static ComboBox Combo<T>(int width = 220) where T : struct, Enum
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Font = Body, FlatStyle = FlatStyle.Flat, FormattingEnabled = true };
        foreach (var v in Enum.GetValues<T>()) c.Items.Add(v);
        c.Format += (_, e) => { if (e.ListItem is Enum v) e.Value = Friendly(v); };
        return c;
    }

    /// <summary>"NowPlaying" → "Now playing", with a few hand-written names.</summary>
    public static string Friendly(Enum value) => value switch
    {
        ScreenMode.Auto => "Auto (stats in games)",
        ScreenMode.DockDefault => "be quiet! default screen",
        ScreenMode.Clock or ScreenKind.Clock => "Clock & calendar",
        Darkmount.Screens.AnimationKind.Gif => "GIF file",
        _ => System.Text.RegularExpressions.Regex.Replace(value.ToString(), "(?<=[a-z0-9])([A-Z])", m => " " + m.Value.ToLowerInvariant()),
    };

    public static NumericUpDown Number(decimal min, decimal max, decimal step = 1, int decimals = 0) =>
        new() { Minimum = min, Maximum = max, Increment = step, DecimalPlaces = decimals, Width = 90, Font = Body };

    public static CheckBox Check(string text) => new() { Text = text, AutoSize = true, ForeColor = Ui.Text, Font = Body, UseMnemonic = false };

    public static Button Button(string text, EventHandler? onClick = null, bool primary = false)
    {
        var b = new Button
        {
            UseMnemonic = false, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Text = text, AutoSize = true, MinimumSize = new Size(100, 34), Font = Body, FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Panel, ForeColor = primary ? Color.Black : Text, Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = primary ? Accent : PanelHover;
        if (onClick is not null) b.Click += onClick;
        return b;
    }

    public static decimal Clamp(NumericUpDown n, double v) => Math.Clamp((decimal)v, n.Minimum, n.Maximum);

    /// <summary>
    /// Starts building a window laid out in 96-DPI units. Layout stays suspended until <see cref="EndLayout"/>, which
    /// scales everything to the screen (175 % on a typical 4K monitor) in one go. Switching scaling on before the
    /// controls exist would scale only the empty window and leave every control at 100 % next to full-size text.
    /// </summary>
    public static void BeginLayout(Form form)
    {
        form.SuspendLayout();
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.AutoScaleMode = AutoScaleMode.Dpi;
    }

    /// <summary>
    /// Ends <see cref="BeginLayout"/>: scales the finished window to the screen and lays it out. A window that would be
    /// bigger than the screen (small laptop screen at high scaling) is shrunk to fit when it opens.
    /// </summary>
    public static void EndLayout(Form form)
    {
        form.ResumeLayout(false);
        form.PerformLayout();
        MarkScaled(form);
        GuardWheel(form);
        form.Load += (_, _) =>
        {
            if (form.WindowState != FormWindowState.Normal) return;
            var area = Screen.FromControl(form).WorkingArea;
            if (form.Width <= area.Width && form.Height <= area.Height) return;
            form.Size = new Size(Math.Min(form.Width, area.Width), Math.Min(form.Height, area.Height));
            form.Location = new Point(area.Left + (area.Width - form.Width) / 2, area.Top + (area.Height - form.Height) / 2);
        };
    }

    /// <summary>Asks for one line of text; null when cancelled. Sizes itself to its text at any Windows scaling.</summary>
    public static string? Prompt(IWin32Window? owner, string title, string label, string initial = "")
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, BackColor = Back, ForeColor = Ui.Text, Font = Body,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        BeginLayout(f);
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(16, 14, 16, 12) };
        var box = new TextBox { Text = initial, Width = 420, Font = Body, Margin = new Padding(0, 6, 0, 14) };
        var ok = Button("OK", primary: true);
        ok.DialogResult = DialogResult.OK;
        var cancel = Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
        buttons.Controls.AddRange([cancel, ok]);
        layout.Controls.Add(new Label { Text = label, AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = Ui.Text, Font = Body, UseMnemonic = false, Margin = new Padding(0) });
        layout.Controls.Add(box);
        layout.Controls.Add(buttons);
        f.Controls.Add(layout);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        EndLayout(f);
        return f.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }

    /// <summary>Controls already scaled to the screen (by their window's <see cref="EndLayout"/> or by <see cref="Add"/>).</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> Scaled = new();

    static void MarkScaled(Control c)
    {
        Scaled.AddOrUpdate(c, Scaled);
        foreach (Control child in c.Controls) MarkScaled(child);
    }

    /// <summary>
    /// Adds a control to a window that is already on screen. WinForms scales a window's controls only once, when it's
    /// built, so a control created (or first shown) later is still in 96-DPI units: it's scaled here first. Controls
    /// that were part of the window when it was scaled are added as they are.
    /// </summary>
    public static void Add(Control parent, Control child)
    {
        if (!Scaled.TryGetValue(child, out _) && parent.FindForm() is { } form && Scaled.TryGetValue(form, out _))
        {
            float factor = parent.DeviceDpi / 96f;
            if (Math.Abs(factor - 1) > 0.001f) child.Scale(new SizeF(factor, factor));
            MarkScaled(child);
        }
        GuardWheel(child);
        parent.Controls.Add(child);
    }

    /// <summary>
    /// Runs a change that shows, hides or resizes many controls (switching pages, sections, layers) with every layout in
    /// <paramref name="root"/> held until the end: one layout pass instead of one per control. Showing the lighting page
    /// went from ~1.5 s to ~0.2 s this way.
    /// </summary>
    public static void Batch(Control root, Action change)
    {
        var containers = new List<Control>();
        void Collect(Control c)
        {
            if (c.Controls.Count == 0) return;
            containers.Add(c);
            foreach (Control child in c.Controls) Collect(child);
        }
        Collect(root);
        foreach (var c in containers) c.SuspendLayout();
        try { change(); }
        finally
        {
            for (int i = containers.Count - 1; i >= 0; i--) containers[i].ResumeLayout(false); // children first
            root.PerformLayout();
        }
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> WheelGuarded = new();

    /// <summary>
    /// Drop-downs and sliders ignore the mouse wheel and scroll the page instead. Windows sends the wheel to the control
    /// under the pointer, so scrolling a long page used to flip a drop-down it passed over (Gradient → Dual → Single),
    /// and every flip rewrote the keyboard's lighting. An open drop-down list still scrolls as usual.
    /// </summary>
    public static void GuardWheel(Control root)
    {
        if (root is ComboBox or TrackBar && !WheelGuarded.TryGetValue(root, out _))
        {
            WheelGuarded.AddOrUpdate(root, WheelGuarded);
            root.MouseWheel += OnGuardedWheel;
        }
        foreach (Control child in root.Controls) GuardWheel(child);
    }

    static void OnGuardedWheel(object? sender, MouseEventArgs e)
    {
        if (sender is ComboBox { DroppedDown: true } || sender is not Control control) return;
        if (e is HandledMouseEventArgs handled) handled.Handled = true;  // the value stays as it is
        Control? scroller = control.Parent;
        while (scroller is not null && scroller is not ScrollableControl { AutoScroll: true }) scroller = scroller.Parent;
        if (scroller is ScrollableControl page)
        {
            var at = page.AutoScrollPosition;                             // negative offsets
            page.AutoScrollPosition = new Point(-at.X, Math.Max(0, -at.Y - e.Delta));
        }
    }

    /// <inheritdoc cref="Add"/>
    public static void AddRange(Control parent, params Control[] children)
    {
        foreach (var child in children) Add(parent, child);
    }

    /// <summary>Asks Windows 10/11 for a dark title bar.</summary>
    public static void UseDarkTitleBar(Form form)
    {
        int on = 1;
        _ = DwmSetWindowAttribute(form.Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
