using System.Drawing.Drawing2D;

namespace VgnBattery;

internal sealed class SettingsDisclosureButton : Button
{
    private bool expanded;
    private bool hovered;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Expanded
    {
        get => expanded;
        set
        {
            expanded = value;
            AccessibleName = expanded ? "收起设置" : "展开设置";
            AccessibleDescription = expanded ? "设置已展开，按 Enter 或空格收起" : "设置已收起，按 Enter 或空格展开";
            Invalidate();
        }
    }

    internal SettingsDisclosureButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = "设置";
        UseMnemonic = false;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = SettingsPalette.Card;
        ForeColor = SettingsPalette.Text;
        Cursor = Cursors.Hand;
        Expanded = false;
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        float scale = DeviceDpi / 96F;
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (hovered)
        {
            using var path = SettingsPalette.Round(new RectangleF(0, 0, Width - 1, Height - 1), 5 * scale);
            using var fill = new SolidBrush(SettingsPalette.Hover);
            e.Graphics.FillPath(fill, path);
        }
        int inset = (int)Math.Round(4 * scale);
        TextRenderer.DrawText(e.Graphics, Text, Font,
            new Rectangle(inset, 0, Math.Max(1, Width - inset - (int)(24 * scale)), Height),
            ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        float x = Width - 12 * scale, y = Height / 2F;
        float direction = expanded ? -1 : 1;
        using var arrow = new Pen(SettingsPalette.Muted, Math.Max(1.3F, scale))
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(arrow,
        [
            new PointF(x - 4 * scale, y - direction * 2 * scale),
            new PointF(x, y + direction * 2 * scale),
            new PointF(x + 4 * scale, y - direction * 2 * scale)
        ]);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -inset, -inset), SettingsPalette.Muted, BackColor);
    }
}
