using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Darkmount.App.Pages;

/// <summary>A square tile with an icon (Segoe MDL2 / Fluent glyph) and a caption; glows when selected.</summary>
public sealed class IconTile : Control
{
    static readonly string IconFont = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    bool _selected, _hover;

    public IconTile(char glyph, string caption)
    {
        Glyph = glyph;
        Text = caption;
        Size = new Size(104, 78);
        Margin = new Padding(0, 0, 10, 10);
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        BackColor = Ui.Back;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public char Glyph { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var r = new RectangleF(1, 1, Width - 3, Height - 3);
        using var path = Round(r, 8);
        var top = _selected ? Color.FromArgb(70, 40, 10) : _hover ? Color.FromArgb(44, 49, 58) : Color.FromArgb(32, 35, 42);
        using (var b = new LinearGradientBrush(r, top, Color.FromArgb(20, 22, 27), LinearGradientMode.Vertical)) g.FillPath(b, path);
        using (var pen = new Pen(_selected ? Ui.Accent : _hover ? Color.FromArgb(0, 200, 255) : Color.FromArgb(60, 65, 76), _selected ? 2f : 1f))
            g.DrawPath(pen, path);

        using var icon = new Font(IconFont, Height * 0.24f, GraphicsUnit.Pixel);
        using var text = new Font("Segoe UI Semibold", 8.5f);
        using var iconBrush = new SolidBrush(_selected ? Ui.Accent : Color.FromArgb(0, 200, 255));
        using var textBrush = new SolidBrush(Ui.Text);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Glyph.ToString(), icon, iconBrush, new RectangleF(0, Height * 0.08f, Width, Height * 0.5f), fmt);
        g.DrawString(Text, text, textBrush, new RectangleF(2, Height * 0.56f, Width - 4, Height * 0.4f), fmt);
    }

    static GraphicsPath Round(RectangleF r, float radius)
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
}

/// <summary>A pill-shaped on/off switch.</summary>
public sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch(string text)
    {
        Text = text;
        AutoSize = false;
        Size = new Size(300, 30);
        Cursor = Cursors.Hand;
        ForeColor = Ui.Text;
        Font = Ui.Body;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float h = Math.Min(LogicalToDeviceUnits(22), Height - LogicalToDeviceUnits(6)), w = h * 1.9f, y = (Height - h) / 2;
        var track = new RectangleF(2, y, w, h);
        using var path = new GraphicsPath();
        path.AddArc(track.X, track.Y, h, h, 90, 180);
        path.AddArc(track.Right - h, track.Y, h, h, 270, 180);
        path.CloseFigure();
        using (var b = new SolidBrush(Checked ? Ui.Accent : Color.FromArgb(58, 62, 72))) g.FillPath(b, path);
        float knob = h - 6, kx = Checked ? track.Right - knob - 3 : track.X + 3;
        using (var kb = new SolidBrush(Color.White)) g.FillEllipse(kb, kx, y + 3, knob, knob);
        using var tb = new SolidBrush(ForeColor);
        using var fmt = new StringFormat { LineAlignment = StringAlignment.Center };
        g.DrawString(Text, Font, tb, new RectangleF(w + 12, 0, Width - w - 12, Height), fmt);
    }
}

/// <summary>A dark rounded "card" panel with an orange caption.</summary>
public sealed class Card : FlowLayoutPanel
{
    public Card(string caption)
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        BackColor = Color.FromArgb(24, 27, 32);
        Padding = new Padding(18, 14, 18, 16);
        Margin = new Padding(0, 0, 16, 16);
        Controls.Add(new Label
        {
            Text = caption.ToUpperInvariant(), AutoSize = true, ForeColor = Ui.Accent,
            Font = new Font("Segoe UI Semibold", 9f), Margin = new Padding(0, 0, 0, 8),
        });
    }
}
