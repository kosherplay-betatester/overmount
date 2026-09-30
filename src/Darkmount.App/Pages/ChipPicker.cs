namespace Darkmount.App.Pages;

/// <summary>
/// A wrapping row of clickable chips drawn by one control, for long lists (90 lighting presets, 48 effects). As separate
/// Buttons every layout pass re-measured each one, and the lighting page took seconds to appear; here the text is
/// measured once and the chips are simply painted. Mouse, keyboard (Tab in, arrows, Enter/Space) and tooltips work like
/// buttons; sizes follow the screen's scaling.
/// </summary>
public sealed class ChipPicker : Control
{
    public sealed record Chip(string Text, string? Tooltip, object? Tag);

    readonly List<Chip> _chips = [];
    readonly List<Rectangle> _bounds = [];
    readonly ToolTip _tip = new() { InitialDelay = 500 };
    int _hover = -1, _focus = -1;
    object? _selected;
    int _wrapWidth;

    /// <param name="wrapWidth">Row width in 96-DPI units; chips wrap onto new rows beyond it.</param>
    /// <param name="minChipWidth">Narrowest chip in 96-DPI units, so short names still make an easy target.</param>
    public ChipPicker(int wrapWidth, int minChipWidth = 112)
    {
        _wrapWidth = wrapWidth;
        MinChipWidth = minChipWidth;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Ui.Back;
        ForeColor = Ui.Text;
        Font = Ui.Body;
        Cursor = Cursors.Hand;
        Disposed += (_, _) => _tip.Dispose();
    }

    public int MinChipWidth { get; }

    public event Action<Chip>? ChipClicked;

    /// <summary>Right-click, the menu key or Shift+F10 on a chip; the point is where a menu should open (screen coordinates).</summary>
    public event Action<Chip, Point>? ChipMenu;

    public IReadOnlyList<Chip> Chips => _chips;

    public void SetChips(IEnumerable<Chip> chips)
    {
        _chips.Clear();
        _chips.AddRange(chips);
        _hover = _focus = -1;
        Relayout();
    }

    /// <summary>The chip whose <see cref="Chip.Tag"/> equals this is drawn highlighted (the current effect).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public object? Selected
    {
        get => _selected;
        set { if (!Equals(_selected, value)) { _selected = value; Invalidate(); } }
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Relayout(); }

    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        _wrapWidth = (int)Math.Round(_wrapWidth * factor.Width); // keep the wrap width in step with WinForms' scaling
        base.ScaleControl(factor, specified);
        Relayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Relayout(); }

    int Px(int logical) => LogicalToDeviceUnits(logical);

    /// <summary>Measures every chip once and sizes the control to hold all rows.</summary>
    void Relayout()
    {
        _bounds.Clear();
        int gap = Px(6), height = Px(34), pad = Px(24), min = Px(MinChipWidth);
        int maxRow = Math.Max(min, _wrapWidth), x = 0, y = 0, widest = 0;
        foreach (var chip in _chips)
        {
            int w = Math.Max(min, TextRenderer.MeasureText(chip.Text, Font).Width + pad);
            if (x > 0 && x + w > maxRow) { x = 0; y += height + gap; }
            _bounds.Add(new Rectangle(x, y, w, height));
            x += w + gap;
            widest = Math.Max(widest, x - gap);
        }
        Size = _chips.Count == 0 ? new Size(1, 1) : new Size(widest + 1, y + height + 1);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using var border = new Pen(Ui.PanelHover);
        using var focus = new Pen(Ui.Accent) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        for (int i = 0; i < _chips.Count; i++)
        {
            var r = _bounds[i];
            if (!e.ClipRectangle.IntersectsWith(r)) continue;
            bool selected = _chips[i].Tag is { } tag && Equals(tag, _selected);
            var fill = selected ? Ui.Accent : i == _hover ? Ui.PanelHover : Ui.Panel;
            using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
            g.DrawRectangle(selected ? focus : border, r.X, r.Y, r.Width - 1, r.Height - 1);
            TextRenderer.DrawText(g, _chips[i].Text, Font, r, selected ? Color.Black : ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            if (i == _focus && Focused) g.DrawRectangle(focus, r.X + 2, r.Y + 2, r.Width - 5, r.Height - 5);
        }
    }

    int HitTest(Point p) => _bounds.FindIndex(r => r.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = HitTest(e.Location);
        if (i == _hover) return;
        _hover = i;
        _tip.SetToolTip(this, i >= 0 ? _chips[i].Tooltip : null);
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int i = HitTest(e.Location);
        if (i < 0 || e.Button is not (MouseButtons.Left or MouseButtons.Right)) return;
        _focus = i;
        Focus();
        if (e.Button == MouseButtons.Right) ChipMenu?.Invoke(_chips[i], PointToScreen(e.Location));
        else ChipClicked?.Invoke(_chips[i]);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_chips.Count == 0) return;
        int f = Math.Max(0, _focus);
        switch (e.KeyCode)
        {
            case Keys.Left: f = Math.Max(0, f - 1); break;
            case Keys.Right: f = Math.Min(_chips.Count - 1, f + 1); break;
            case Keys.Up or Keys.Down:
                var from = _bounds[f];
                int dir = e.KeyCode == Keys.Up ? -1 : 1;
                var row = _bounds.Select((r, i) => (r, i)).Where(t => Math.Sign(t.r.Y - from.Y) == dir).ToList();
                if (row.Count > 0)
                {
                    int nearestY = dir < 0 ? row.Max(t => t.r.Y) : row.Min(t => t.r.Y);
                    f = row.Where(t => t.r.Y == nearestY).MinBy(t => Math.Abs(t.r.X - from.X)).i;
                }
                break;
            case Keys.Enter or Keys.Space:
                if (_focus >= 0) ChipClicked?.Invoke(_chips[_focus]);
                e.Handled = true;
                return;
            case Keys.Apps:
            case Keys.F10 when e.Shift:
                if (_focus >= 0 && _focus < _bounds.Count)
                    ChipMenu?.Invoke(_chips[_focus], PointToScreen(new Point(_bounds[_focus].Left, _bounds[_focus].Bottom)));
                e.Handled = true;
                return;
            default: return;
        }
        e.Handled = true;
        _focus = f;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); if (_focus < 0 && _chips.Count > 0) _focus = 0; Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}
