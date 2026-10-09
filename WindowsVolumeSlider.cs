using System.Drawing.Drawing2D;

namespace ChannelBridge;

internal sealed class WindowsVolumeSlider : Control
{
    int value, wheel;
    bool hover;
    public event EventHandler? ValueChanged;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Value { get => value; set { int next = Math.Clamp(value, 0, 100); if (this.value == next) return; this.value = next; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1); } }
    public WindowsVolumeSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.Slider;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); float scale = DeviceDpi / 96f, pad = 9 * scale, y = Height / 2f;
        float end = Math.Max(pad, Width - pad), x = pad + (end - pad) * Value / 100;
        Color accent = Enabled ? Color.FromArgb(0, 120, 215) : Color.FromArgb(170, 170, 170);
        using var rest = new Pen(Enabled ? Color.FromArgb(130, 130, 130) : Color.LightGray, 2 * scale);
        using var filled = new Pen(accent, 2 * scale);
        e.Graphics.DrawLine(rest, pad, y, end, y); e.Graphics.DrawLine(filled, pad, y, x, y);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float w = 6 * scale, h = Math.Min(24 * scale, Height - 6 * scale), radius = 2 * scale;
        using var path = new GraphicsPath();
        path.AddArc(x - w / 2, y - h / 2, radius * 2, radius * 2, 180, 90);
        path.AddArc(x + w / 2 - radius * 2, y - h / 2, radius * 2, radius * 2, 270, 90);
        path.AddArc(x + w / 2 - radius * 2, y + h / 2 - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(x - w / 2, y + h / 2 - radius * 2, radius * 2, radius * 2, 90, 90); path.CloseFigure();
        using var brush = new SolidBrush(Enabled && (hover || Capture) ? Color.FromArgb(0, 100, 180) : accent); e.Graphics.FillPath(brush, path);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2));
    }
    void SetPosition(int x) { float pad = 9 * DeviceDpi / 96f; Value = (int)Math.Round((x - pad) * 100 / Math.Max(1, Width - 2 * pad)); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Focus(); Capture = true; SetPosition(e.X); } }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (Capture) SetPosition(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) { Capture = false; Invalidate(); } }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode) { case Keys.Left: case Keys.Down: Value--; break; case Keys.Right: case Keys.Up: Value++; break; case Keys.PageUp: Value += 10; break; case Keys.PageDown: Value -= 10; break; case Keys.Home: Value = 0; break; case Keys.End: Value = 100; break; default: return; }
        e.Handled = e.SuppressKeyPress = true;
    }
    internal void Wheel(int delta) { if (!Enabled) return; wheel += delta; int steps = wheel / 120; wheel %= 120; Value += steps * 2; }
    protected override void OnMouseWheel(MouseEventArgs e) { Wheel(e.Delta); if (e is HandledMouseEventArgs h) h.Handled = true; }
    protected override AccessibleObject CreateAccessibilityInstance() => new SliderAccessible(this);
    sealed class SliderAccessible(WindowsVolumeSlider owner) : ControlAccessibleObject(owner)
    {
        public override string? Value { get => owner.Value.ToString(); set { if (owner.Enabled && int.TryParse(value, out int n)) owner.Value = n; } }
    }
}

internal sealed class WindowsVolumeButton : Button
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Muted { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Level { get; set; }
    public WindowsVolumeButton() { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand; SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Math.Min(Width / 40f, Height / 36f); var state = e.Graphics.Save();
        e.Graphics.TranslateTransform((Width - 36 * s) / 2, (Height - 30 * s) / 2); e.Graphics.ScaleTransform(s, s);
        using var p = new Pen(Enabled ? Color.FromArgb(30, 30, 30) : Color.Gray, 1.7f) { LineJoin = LineJoin.Round };
        e.Graphics.DrawPolygon(p, new PointF[] { new(2,11),new(7,11),new(13,5),new(13,25),new(7,19),new(2,19) });
        if (Muted || Level == 0) { e.Graphics.DrawLine(p, 19, 11, 27, 19); e.Graphics.DrawLine(p, 27, 11, 19, 19); }
        else { e.Graphics.DrawArc(p, 10, 10, 12, 10, -60, 120); if (Level > 33) e.Graphics.DrawArc(p, 7, 6, 22, 18, -55, 110); if (Level > 66) e.Graphics.DrawArc(p, 3, 2, 32, 26, -48, 96); }
        e.Graphics.Restore(state);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2));
    }
}
