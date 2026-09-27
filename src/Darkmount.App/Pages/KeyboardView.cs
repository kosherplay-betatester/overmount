using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Darkmount.App.Pages;

/// <summary>
/// A key on the keyboard picture: id, position/size in layout units (1 unit = one standard key), label. <see cref="Led"/>
/// shapes are edge LEDs, drawn as small glowing bars instead of keycaps.
/// </summary>
public sealed record KeyShape(int Id, RectangleF Rect, string Label, bool Led = false);

/// <summary>
/// Draws the keyboard from layout geometry, scaled to fit. Keys can be selected (single or multi with Ctrl/drag),
/// tinted with per-key colours (lighting) and marked (e.g. remapped keys).
/// </summary>
public sealed class KeyboardView : Control
{
    IReadOnlyList<KeyShape> _keys = [];
    readonly HashSet<int> _selected = [];
    readonly Dictionary<int, Color> _colors = [];
    readonly HashSet<int> _marked = [];
    readonly Dictionary<int, string> _subLabels = [];
    int _hover = -1;
    RectangleF _bounds;
    float _scale = 1;
    PointF _offset;
    Point? _dragStart;
    Rectangle _dragRect;

    public KeyboardView()
    {
        DoubleBuffered = true;
        BackColor = Ui.Back;
        MinimumSize = new Size(600, 200);
    }

    /// <summary>Allow selecting several keys (Ctrl+click, drag a box). Otherwise a click selects exactly one key.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool MultiSelect { get; set; }

    public event Action? SelectionChanged;

    public IReadOnlyCollection<int> SelectedKeys => _selected;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<KeyShape> KeyShapes
    {
        get => _keys;
        set
        {
            _keys = value;
            _bounds = value.Count == 0 ? RectangleF.Empty : value.Select(k => k.Rect).Aggregate(RectangleF.Union);
            Relayout();
            Invalidate();
        }
    }

    public void SetColors(IReadOnlyDictionary<int, Color> colors)
    {
        _colors.Clear();
        foreach (var (id, c) in colors) _colors[id] = c;
        Invalidate();
    }

    /// <summary>Small second line on a keycap (e.g. what a remapped key now does).</summary>
    public void SetSubLabels(IReadOnlyDictionary<int, string> labels)
    {
        _subLabels.Clear();
        foreach (var (id, text) in labels) _subLabels[id] = text;
        Invalidate();
    }

    /// <summary>Outline the selected keys (off for screenshots of the lighting alone).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowSelection { get; set; } = true;

    /// <summary>Draw a chassis with an RGB underglow behind the keys (gamer look).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Chassis { get; set; } = true;

    public void SetMarked(IEnumerable<int> ids)
    {
        _marked.Clear();
        _marked.UnionWith(ids);
        Invalidate();
    }

    public void Select(IEnumerable<int> ids)
    {
        _selected.Clear();
        _selected.UnionWith(ids);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    void Relayout()
    {
        if (_bounds.Width <= 0 || Width <= 0) return;
        float pad = Chassis ? Math.Max(LogicalToDeviceUnits(18), Height * 0.07f) : LogicalToDeviceUnits(8);
        _scale = Math.Min((Width - 2 * pad) / _bounds.Width, (Height - 2 * pad) / _bounds.Height);
        _offset = new PointF(pad + (Width - 2 * pad - _bounds.Width * _scale) / 2 - _bounds.X * _scale,
                             pad + (Height - 2 * pad - _bounds.Height * _scale) / 2 - _bounds.Y * _scale);
    }

    RectangleF ToScreen(RectangleF r) =>
        new(_offset.X + r.X * _scale + 1.5f, _offset.Y + r.Y * _scale + 1.5f, r.Width * _scale - 3, r.Height * _scale - 3);

    int HitTest(Point p) =>
        _keys.FirstOrDefault(k => !k.Led && ToScreen(k.Rect).Contains(p))?.Id
        ?? _keys.FirstOrDefault(k => k.Led && RectangleF.Inflate(ToScreen(k.Rect), 4, 4).Contains(p))?.Id ?? -1;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        if (_keys.Count == 0) return;

        if (Chassis) PaintChassis(g);

        // Text size follows the size of a standard key on screen.
        var heights = _keys.Where(k => !k.Led).Select(k => ToScreen(k.Rect).Height).Where(h => h > 3).OrderBy(h => h).ToList();
        float keyPx = heights.Count > 0 ? heights[heights.Count / 2] : 20;
        float fontPx = Math.Clamp(keyPx * 0.36f, 9f, 28f);
        using var font = new Font("Segoe UI Semibold", fontPx, GraphicsUnit.Pixel);
        using var subFont = new Font("Segoe UI Semibold", Math.Max(7f, fontPx * 0.72f), GraphicsUnit.Pixel);
        using var fmt = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };

        foreach (var key in _keys.Where(k => k.Led)) PaintLed(g, key);
        foreach (var key in _keys)
        {
            if (key.Led) continue;
            var r = ToScreen(key.Rect);
            if (r.Width < 3 || r.Height < 3) continue;
            bool selected = ShowSelection && _selected.Contains(key.Id), hover = key.Id == _hover;
            float radius = Math.Max(3, Math.Min(r.Width, r.Height) * 0.16f);

            // Neon glow behind a selected key (only when few keys are selected, so a whole-keyboard layer stays readable).
            if (selected && _selected.Count <= 24)
                for (int i = 3; i >= 1; i--)
                {
                    var glow = RectangleF.Inflate(r, i * 2f, i * 2f);
                    using var gp = Rounded(glow, radius + i * 2);
                    using var gb = new SolidBrush(Color.FromArgb(24, Ui.Accent));
                    g.FillPath(gb, gp);
                }

            using var path = Rounded(r, radius);
            bool lit = _colors.TryGetValue(key.Id, out var led);
            var top = lit ? Blend(led, Color.FromArgb(40, 44, 52), 0.35f) : Color.FromArgb(hover ? 56 : 44, hover ? 61 : 49, hover ? 72 : 58);
            var bottom = lit ? Blend(led, Color.FromArgb(14, 16, 20), 0.6f) : Color.FromArgb(20, 22, 27);
            using (var b = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical)) g.FillPath(b, path);

            // Keycap top face (inset) for a 3D look.
            var face = new RectangleF(r.X + r.Width * 0.08f, r.Y + r.Height * 0.06f, r.Width * 0.84f, r.Height * 0.74f);
            using (var fp = Rounded(face, radius * 0.8f))
            using (var fb = new LinearGradientBrush(face, Color.FromArgb(lit ? 60 : 38, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                g.FillPath(fb, fp);

            var border = selected ? Ui.Accent : hover ? Color.FromArgb(0, 200, 255) : Color.FromArgb(70, 76, 88);
            using (var pen = new Pen(border, selected ? 1.8f : hover ? 1.6f : 1f)) g.DrawPath(pen, path);

            if (_marked.Contains(key.Id))
            {
                float d = Math.Max(5, r.Width * 0.14f);
                using var dot = new SolidBrush(Ui.Accent);
                g.FillEllipse(dot, r.Right - d - 3, r.Top + 3, d, d);
            }

            using var text = new SolidBrush(selected ? Color.White : lit ? Color.White : Ui.Text);
            if (_subLabels.TryGetValue(key.Id, out var sub) && r.Height > 22)
            {
                var upper = new RectangleF(r.X + 2, r.Y + 2, r.Width - 4, r.Height * 0.55f);
                var lower = new RectangleF(r.X + 2, r.Y + r.Height * 0.52f, r.Width - 4, r.Height * 0.44f);
                DrawFitted(g, key.Label, font, text, upper, fmt);
                using var accent = new SolidBrush(Color.FromArgb(255, 170, 70));
                DrawFitted(g, sub, subFont, accent, lower, fmt);
            }
            else DrawFitted(g, key.Label, font, text, face, fmt);
        }

        if (_dragStart is not null && _dragRect.Width > 2)
        {
            using var sel = new Pen(Ui.Accent) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(sel, _dragRect);
        }
    }

    /// <summary>An edge LED: a small bar in its colour with a soft glow, ringed when selected or hovered.</summary>
    void PaintLed(Graphics g, KeyShape key)
    {
        var r = new RectangleF(_offset.X + key.Rect.X * _scale, _offset.Y + key.Rect.Y * _scale, key.Rect.Width * _scale, key.Rect.Height * _scale);
        bool selected = ShowSelection && _selected.Contains(key.Id), hover = key.Id == _hover;
        bool lit = _colors.TryGetValue(key.Id, out var c) && c.R + c.G + c.B > 24;
        float radius = Math.Max(1, Math.Min(r.Width, r.Height) / 2);
        if (lit)
            for (int i = 3; i >= 1; i--)
            {
                using var halo = Rounded(RectangleF.Inflate(r, i * 2f, i * 2f), radius + i * 2);
                using var hb = new SolidBrush(Color.FromArgb(34, c));
                g.FillPath(hb, halo);
            }
        using var path = Rounded(r, radius);
        using (var b = new SolidBrush(lit ? c : Color.FromArgb(46, 50, 58))) g.FillPath(b, path);
        if (selected || hover)
        {
            using var ring = Rounded(RectangleF.Inflate(r, 2.5f, 2.5f), radius + 2.5f);
            using var pen = new Pen(selected ? Ui.Accent : Color.FromArgb(0, 200, 255), selected ? 2f : 1.4f);
            g.DrawPath(pen, ring);
        }
    }

    /// <summary>Draws text on one line, shrinking the font (down to 55 %) when it is wider than the key.</summary>
    static void DrawFitted(Graphics g, string text, Font font, Brush brush, RectangleF r, StringFormat fmt)
    {
        var size = g.MeasureString(text, font);
        if (size.Width <= r.Width || size.Width <= 0) { g.DrawString(text, font, brush, r, fmt); return; }
        float k = Math.Max(0.55f, r.Width / size.Width);
        using var smaller = new Font(font.FontFamily, font.Size * k, font.Style, GraphicsUnit.Pixel);
        g.DrawString(text, smaller, brush, r, fmt);
    }

    /// <summary>A dark aluminium-like plate with a soft RGB underglow along its bottom edge.</summary>
    void PaintChassis(Graphics g)
    {
        var keys = ToScreen(_bounds);
        float margin = Math.Max(LogicalToDeviceUnits(10), _scale * 0.35f);
        var plate = RectangleF.Inflate(keys, margin, margin);
        var glow = new RectangleF(plate.X + plate.Width * 0.02f, plate.Bottom - 2, plate.Width * 0.96f, Math.Max(6, margin * 0.8f));
        using (var rainbow = new LinearGradientBrush(glow, Color.Red, Color.Blue, LinearGradientMode.Horizontal)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(120, 255, 40, 0), Color.FromArgb(120, 255, 200, 0), Color.FromArgb(120, 0, 220, 120),
                          Color.FromArgb(120, 0, 160, 255), Color.FromArgb(120, 180, 60, 255), Color.FromArgb(120, 255, 40, 0)],
                Positions = [0f, 0.2f, 0.4f, 0.6f, 0.8f, 1f],
            },
        })
        {
            for (int i = 0; i < 3; i++)
            {
                var halo = RectangleF.Inflate(glow, i * 6, i * 3);
                using var hp = Rounded(halo, halo.Height / 2);
                g.FillPath(rainbow, hp);
            }
        }
        using var platePath = Rounded(plate, margin * 0.6f);
        using (var pb = new LinearGradientBrush(plate, Color.FromArgb(34, 37, 44), Color.FromArgb(16, 18, 22), LinearGradientMode.Vertical))
            g.FillPath(pb, platePath);
        using var edge = new Pen(Color.FromArgb(62, 66, 76), 1.2f);
        g.DrawPath(edge, platePath);
    }

    static Color Blend(Color a, Color b, float t) =>
        Color.FromArgb((int)(a.R * (1 - t) + b.R * t), (int)(a.G * (1 - t) + b.G * t), (int)(a.B * (1 - t) + b.B * t));

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStart is { } start && MultiSelect && e.Button == MouseButtons.Left)
        {
            _dragRect = Rectangle.FromLTRB(Math.Min(start.X, e.X), Math.Min(start.Y, e.Y), Math.Max(start.X, e.X), Math.Max(start.Y, e.Y));
            Invalidate();
            return;
        }
        int hover = HitTest(e.Location);
        if (hover != _hover) { _hover = hover; Invalidate(); }
        Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { _dragStart = e.Location; _dragRect = Rectangle.Empty; }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        bool ctrl = (ModifierKeys & Keys.Control) != 0;
        if (MultiSelect && _dragRect.Width > 6 && _dragRect.Height > 6)
        {
            if (!ctrl) _selected.Clear();
            foreach (var k in _keys.Where(k => _dragRect.IntersectsWith(Rectangle.Round(ToScreen(k.Rect))))) _selected.Add(k.Id);
        }
        else
        {
            int id = HitTest(e.Location);
            if (id >= 0)
            {
                if (MultiSelect && ctrl) { if (!_selected.Remove(id)) _selected.Add(id); }
                else { _selected.Clear(); _selected.Add(id); }
            }
        }
        _dragStart = null;
        _dragRect = Rectangle.Empty;
        Invalidate();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }
}
