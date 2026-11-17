using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace VgnBattery;

internal sealed record ChoiceOption(string Value, string Text, bool Enabled = true);

internal sealed class StyledChoiceButton : Button
{
    private IReadOnlyList<ChoiceOption> choices = [];
    private string choicesKey = "";
    private string selectedValue = "";
    private ContextMenuStrip? openMenu;
    private bool hovered;
    internal event EventHandler? SelectionChanged;
    internal string SelectedValue => selectedValue;

    internal StyledChoiceButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = SettingsPalette.Field;
        ForeColor = SettingsPalette.Text;
        Cursor = Cursors.Hand;
        TextAlign = ContentAlignment.MiddleLeft;
    }

    internal void SetChoices(IReadOnlyList<ChoiceOption> options, string selected)
    {
        string key = string.Join("\n", options.Select(o => o.Value + "|" + o.Text + "|" + o.Enabled));
        if (choicesKey == key && selectedValue == selected) return;
        choices = options;
        choicesKey = key;
        selectedValue = selected;
        Text = choices.FirstOrDefault(c => c.Value == selected)?.Text ?? "请选择";
        AccessibleDescription = Text;
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (!Enabled || choices.Count == 0) return;
        openMenu?.Dispose();
        var dropdown = new ContextMenuStrip
        {
            ShowImageMargin = false, ShowCheckMargin = true, Renderer = new DarkMenuRenderer(),
            BackColor = SettingsPalette.Card, ForeColor = SettingsPalette.Text,
            Font = Font, Padding = new Padding(4)
        };
        foreach (var option in choices)
        {
            var item = new ToolStripMenuItem(option.Text) { Checked = option.Value == selectedValue, Enabled = option.Enabled };
            item.Click += (_, _) =>
            {
                if (selectedValue == option.Value) return;
                selectedValue = option.Value;
                Text = option.Text;
                AccessibleDescription = option.Text;
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            };
            dropdown.Items.Add(item);
        }
        dropdown.Closed += (_, _) =>
        {
            if (openMenu == dropdown) openMenu = null;
            // Choosing an option can resize the popup, which closes this drop-down
            // while it is still running its own click handling. Disposing it here
            // makes that handling touch a dead object, so wait for the message loop.
            if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(dropdown.Dispose));
            else dropdown.Dispose();
        };
        openMenu = dropdown;
        dropdown.Show(this, new Point(0, Height));
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        float scale = DeviceDpi / 96F;
        e.Graphics.Clear(Parent?.BackColor ?? SettingsPalette.Card);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = SettingsPalette.Round(new RectangleF(.5F, .5F, Width - 1, Height - 1), 6 * scale);
        using var fill = new SolidBrush(hovered && Enabled ? SettingsPalette.Hover : BackColor);
        e.Graphics.FillPath(fill, path);
        using var border = new Pen(SettingsPalette.Border, Math.Max(1, scale));
        e.Graphics.DrawPath(border, path);
        int inset = (int)Math.Round(9 * scale);
        int arrowWidth = (int)Math.Round(22 * scale);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(inset, 0, Math.Max(1, Width - inset - arrowWidth), Height),
            Enabled ? ForeColor : SettingsPalette.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        using var arrow = new Pen(SettingsPalette.Muted, Math.Max(1.2F, scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = Width - arrowWidth / 2F, cy = Height / 2F;
        e.Graphics.DrawLines(arrow, [new PointF(cx - 3 * scale, cy - scale), new PointF(cx, cy + 2 * scale), new PointF(cx + 3 * scale, cy - scale)]);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -(int)(4 * scale), -(int)(4 * scale)), SettingsPalette.Muted, BackColor);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { openMenu?.Dispose(); openMenu = null; }
        base.Dispose(disposing);
    }
}

internal static class SettingsPalette
{
    internal static readonly Color Card = Color.FromArgb(34, 40, 49);
    internal static readonly Color Hover = Color.FromArgb(42, 50, 61);
    internal static readonly Color Field = Color.FromArgb(27, 33, 42);
    internal static readonly Color Border = Color.FromArgb(65, 74, 88);
    internal static readonly Color Text = Color.FromArgb(239, 243, 250);
    internal static readonly Color Muted = Color.FromArgb(158, 168, 183);
    internal static readonly Color Green = Color.FromArgb(115, 227, 167);

    internal static GraphicsPath Round(RectangleF bounds, float radius)
    {
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class SettingsCard : Panel
{
    public SettingsCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = SettingsPalette.Card;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Color.FromArgb(27, 30, 36));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96F;
        using var path = SettingsPalette.Round(new RectangleF(.5F, .5F, Width - 1, Height - 1), 10 * scale);
        using var fill = new SolidBrush(BackColor);
        e.Graphics.FillPath(fill, path);
        using var border = new Pen(Color.FromArgb(49, 58, 70), Math.Max(1, scale));
        e.Graphics.DrawPath(border, path);
    }
}

internal sealed class StyledCheckBox : CheckBox
{
    private readonly bool divider;
    private bool hovered;

    internal StyledCheckBox(bool divider = false)
    {
        this.divider = divider;
        AutoSize = false;
        BackColor = SettingsPalette.Card;
        ForeColor = SettingsPalette.Text;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        float scale = DeviceDpi / 96F;
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (hovered && Enabled)
        {
            using var hover = SettingsPalette.Round(new RectangleF(0, 0, Width - 1, Height - 1), 5 * scale);
            using var hoverFill = new SolidBrush(SettingsPalette.Hover);
            e.Graphics.FillPath(hoverFill, hover);
        }
        int inset = (int)Math.Round(4 * scale);
        int side = (int)Math.Round(17 * scale);
        var check = new RectangleF(Width - side - inset, (Height - side) / 2F, side, side);
        using var box = SettingsPalette.Round(check, 4 * scale);
        if (Checked)
        {
            using var fill = new SolidBrush(Enabled ? SettingsPalette.Green : SettingsPalette.Muted);
            e.Graphics.FillPath(fill, box);
            using var tick = new Pen(Color.FromArgb(21, 54, 38), Math.Max(1.5F, 1.7F * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            e.Graphics.DrawLines(tick,
            [
                new PointF(check.X + side * .23F, check.Y + side * .52F),
                new PointF(check.X + side * .43F, check.Y + side * .71F),
                new PointF(check.X + side * .77F, check.Y + side * .31F)
            ]);
        }
        else
        {
            using var border = new Pen(Enabled ? SettingsPalette.Muted : SettingsPalette.Border, Math.Max(1F, scale));
            e.Graphics.DrawPath(border, box);
        }
        var textBounds = new Rectangle(inset, 0, Math.Max(1, (int)check.Left - inset * 3), Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, Enabled ? ForeColor : SettingsPalette.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        if (divider)
        {
            using var line = new Pen(Color.FromArgb(47, 56, 68));
            e.Graphics.DrawLine(line, inset, Height - 1, Width - inset, Height - 1);
        }
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -inset, -inset), SettingsPalette.Muted, BackColor);
    }
}

internal sealed class StyledButton : Button
{
    private bool hovered;
    private bool pressed;

    internal StyledButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        BackColor = Color.FromArgb(47, 63, 80);
        ForeColor = SettingsPalette.Text;
        Cursor = Cursors.Hand;
        FlatAppearance.BorderSize = 0;
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        float scale = DeviceDpi / 96F;
        e.Graphics.Clear(Parent?.BackColor ?? SettingsPalette.Card);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = SettingsPalette.Round(new RectangleF(.5F, .5F, Width - 1, Height - 1), 6 * scale);
        Color fill = !Enabled ? Color.FromArgb(40, 47, 57) : pressed ? Color.FromArgb(62, 86, 108) : hovered ? Color.FromArgb(57, 77, 97) : BackColor;
        using var brush = new SolidBrush(fill);
        e.Graphics.FillPath(brush, path);
        using var border = new Pen(Enabled ? Color.FromArgb(71, 91, 110) : SettingsPalette.Border, Math.Max(1F, scale));
        e.Graphics.DrawPath(border, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : SettingsPalette.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -(int)(4 * scale), -(int)(4 * scale)), SettingsPalette.Muted, fill);
    }
}

internal sealed class ThresholdStepper : UserControl
{
    private readonly StyledButton minus;
    private readonly StyledButton plus;
    private readonly TextBox input;
    private int value = 20;
    private bool settingText;
    private bool arranging;
    internal event EventHandler? ValueChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => value;
        set => SetValue(value, true);
    }

    public ThresholdStepper()
    {
        SuspendLayout();
        BackColor = SettingsPalette.Field;
        ForeColor = SettingsPalette.Text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        minus = new StyledButton { Text = "−", TabStop = true, AccessibleName = "降低低电提醒阈值", BackColor = SettingsPalette.Field, Font = new Font("Segoe UI", 12F) };
        plus = new StyledButton { Text = "+", TabStop = true, AccessibleName = "提高低电提醒阈值", BackColor = SettingsPalette.Field, Font = new Font("Segoe UI", 12F) };
        input = new TextBox
        {
            Text = "20", BorderStyle = BorderStyle.None, BackColor = SettingsPalette.Field,
            ForeColor = SettingsPalette.Text, TextAlign = HorizontalAlignment.Center,
            MaxLength = 2, AccessibleName = "低电通知百分比阈值"
        };
        minus.Click += (_, _) => Value--;
        plus.Click += (_, _) => Value++;
        input.TextChanged += (_, _) => { if (!settingText && int.TryParse(input.Text, out int entered) && entered is >= 1 and <= 99) SetValue(entered, false); };
        input.Leave += (_, _) => CommitText();
        input.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar)) e.Handled = true; };
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Up or Keys.Down) { Value += e.KeyCode == Keys.Up ? 1 : -1; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter) { CommitText(); e.SuppressKeyPress = true; }
        };
        Controls.AddRange([minus, input, plus]);
        Size = new Size(130, 30);
        AccessibleRole = AccessibleRole.SpinButton;
        AccessibleName = "低电通知百分比阈值";
        ResumeLayout(false);
    }

    private void SetValue(int next, bool updateText)
    {
        next = Math.Clamp(next, 1, 99);
        if (value == next) return;
        value = next;
        if (updateText) FormatText();
        Invalidate();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FormatText()
    {
        settingText = true;
        try { input.Text = value.ToString(); }
        finally { settingText = false; }
    }

    private void CommitText()
    {
        if (int.TryParse(input.Text, out int entered)) SetValue(entered, false);
        FormatText();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ArrangeChildren();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        ArrangeChildren();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ArrangeChildren();
    }

    private void ArrangeChildren()
    {
        if (minus is null || plus is null || input is null) return;
        if (arranging) return;
        arranging = true;
        try
        {
            float scale = DeviceDpi / 96F;
            int edge = Math.Max(1, (int)Math.Round(3 * scale));
            int buttonWidth = (int)Math.Round(27 * scale);
            minus.Bounds = new Rectangle(edge, edge, buttonWidth, Math.Max(1, Height - edge * 2));
            plus.Bounds = new Rectangle(Width - buttonWidth - edge, edge, buttonWidth, Math.Max(1, Height - edge * 2));
            int suffix = (int)Math.Round(15 * scale);
            input.Bounds = new Rectangle(minus.Right + (int)(3 * scale), (Height - input.PreferredHeight) / 2,
                Math.Max(1, plus.Left - minus.Right - (int)(6 * scale) - suffix), input.PreferredHeight);
        }
        finally { arranging = false; }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (input is not null) input.Font = Font;
        OnResize(EventArgs.Empty);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (input is not null) input.Visible = Enabled;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? SettingsPalette.Card);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96F;
        using var path = SettingsPalette.Round(new RectangleF(.5F, .5F, Width - 1, Height - 1), 7 * scale);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
        using var border = new Pen(SettingsPalette.Border, Math.Max(1F, scale));
        e.Graphics.DrawPath(border, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (input is null || plus is null) return;
        var suffix = new Rectangle(input.Right, 0, Math.Max(1, plus.Left - input.Right), Height);
        TextRenderer.DrawText(e.Graphics, "%", Font, suffix, SettingsPalette.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        if (!Enabled)
            TextRenderer.DrawText(e.Graphics, value.ToString(), Font, new Rectangle(input.Left, 0, input.Width, Height), SettingsPalette.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
    }
}
