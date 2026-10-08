using System.Drawing.Drawing2D;

namespace ChannelBridge;

public sealed class SpeakerMap : Panel
{
    public List<SpeakerSetting> Speakers = new();
    public List<Endpoint> Devices = new();
    public Endpoint? Source;
    public string SelectedRole = "FL", LayoutName = "";
    public event Action<string>? SpeakerSelected;
    readonly Dictionary<string, RectangleF> hit = new();
    readonly ToolTip tooltip = new();
    string hovered = "";
    internal float? TestScale;
    float UiScale => TestScale ?? DeviceDpi / 96f;
    static readonly Color Ink = Color.FromArgb(33, 52, 76), Accent = Color.FromArgb(44, 108, 224), Light = Color.FromArgb(221, 231, 243);
    public SpeakerMap()
    {
        DoubleBuffered = true; BackColor = Color.White; TabStop = true; SetStyle(ControlStyles.Selectable, true);
        AccessibleName = "音箱位置图"; AccessibleDescription = "点击音箱或用方向键选择，在右侧编辑其属性。";
    }
    public Point SpeakerCenter(string role) => new((int)((hit[role].X + hit[role].Width / 2) * UiScale), (int)((hit[role].Y + hit[role].Height / 2) * UiScale));
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus(); if (e.Button != MouseButtons.Left) return;
        SelectAt(e.Location);
    }
    internal void SelectAt(Point point)
    {
        var selected = hit.FirstOrDefault(p => p.Value.Contains(new PointF(point.X / UiScale, point.Y / UiScale)));
        if (selected.Key != null) { SelectedRole = selected.Key; SpeakerSelected?.Invoke(selected.Key); Invalidate(); }
    }
    internal void VerifyGeometry()
    {
        var bounds = new RectangleF(0, 0, Width / UiScale, Height / UiScale);
        foreach (var a in hit)
        {
            if (!bounds.Contains(a.Value)) throw new Exception("Speaker clipped: " + a.Key);
            foreach (var b in hit) if (a.Key != b.Key && a.Value.IntersectsWith(b.Value)) throw new Exception("Speakers overlap: " + a.Key + " / " + b.Key);
        }
    }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (Speakers.Count == 0) return;
        int direction = e.KeyCode is Keys.Left or Keys.Up ? -1 : e.KeyCode is Keys.Right or Keys.Down ? 1 : 0;
        if (direction == 0) return; int current = Speakers.FindIndex(s => s.Role == SelectedRole);
        SelectedRole = Speakers[(current + direction + Speakers.Count) % Speakers.Count].Role; SpeakerSelected?.Invoke(SelectedRole); Invalidate(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); string next = hit.FirstOrDefault(p => p.Value.Contains(new PointF(e.X / UiScale, e.Y / UiScale))).Key ?? ""; Cursor = next.Length > 0 ? Cursors.Hand : Cursors.Default;
        if (next == hovered) return; hovered = next; var s = Speakers.FirstOrDefault(s => s.Role == next);
        string deviceName = s == null ? "" : Devices.FirstOrDefault(d => d.Id == s.DeviceId)?.Name ?? UiLanguage.T("未分配设备");
        tooltip.SetToolTip(this, s == null ? "" : UiLanguage.T($"{SpeakerLayouts.Role(s.Role).Name} {s.Role}\n") + deviceName + UiLanguage.T($" / {(s.NativeOutput ? "CH" + (s.Side + 1) : s.Side == 0 ? "L" : "R")}\n{(s.SourceChannel >= 0 ? "音源 CH" + (s.SourceChannel + 1) : "未连接音源")} · {s.GainDb:+0.0;-0.0;0.0} dB · 延迟 {s.DelayMs} ms"));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; var state = g.Save();
        g.ScaleTransform(UiScale, UiScale); g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = Width / UiScale, h = Height / UiScale;
        using var text = new SolidBrush(Ink); using var soft = new SolidBrush(Color.FromArgb(123, 138, 159));
        using var small = new Font("Microsoft YaHei UI", 12, GraphicsUnit.Pixel); using var font = new Font("Microsoft YaHei UI", 13, GraphicsUnit.Pixel); using var strong = new Font("Microsoft YaHei UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
        using var miniStrong = new Font("Microsoft YaHei UI", 12, FontStyle.Bold, GraphicsUnit.Pixel); using var mini = new Font("Microsoft YaHei UI", 11, GraphicsUnit.Pixel);
        g.DrawString(UiLanguage.T("听音空间"), strong, text, 20, 17); g.DrawString(UiLanguage.T("点击音箱，配置对应输出"), small, soft, UiLanguage.English ? 132 : 104, 19);
        var center = new PointF(w * .5f, h * .53f); float radius = Math.Min(w * .29f, h * .33f);
        using var ring = new Pen(Light, 1.2f); using var field = new SolidBrush(Color.FromArgb(248, 251, 255));
        g.FillEllipse(field, center.X - radius, center.Y - radius, radius * 2, radius * 2);
        for (int n = 1; n <= 3; n++) { float r = radius * n / 3; g.DrawEllipse(ring, center.X - r, center.Y - r, r * 2, r * 2); }
        using var ray = new Pen(Color.FromArgb(224, 232, 242), 1) { DashStyle = DashStyle.Dash }; hit.Clear();
        float width = Math.Clamp(w * .155f, 76, 134), height = Math.Clamp((h - 75) * .21f, 44, 84);
        foreach (var s in Speakers)
        {
            var role = SpeakerLayouts.Role(s.Role); float x = 30 + role.X * (w - 60), y = 40 + role.Y * (h - 75);
            var rect = new RectangleF(x - width / 2, y - height / 2, width, height); hit[s.Role] = rect;
            if (s.Role != "LFE") g.DrawLine(ray, center, new PointF(x, y));
        }
        using var chair = new SolidBrush(Color.FromArgb(225, 236, 249)); using var person = new SolidBrush(Color.FromArgb(129, 158, 195));
        g.FillEllipse(chair, center.X - 47, center.Y - 48, 94, 96); g.FillEllipse(person, center.X - 14, center.Y - 25, 28, 28); g.FillPie(person, center.X - 31, center.Y - 4, 62, 53, 180, 180);
        using var arrow = new Pen(Color.FromArgb(136, 163, 195), 2) { EndCap = LineCap.ArrowAnchor }; g.DrawLine(arrow, center.X, center.Y - 57, center.X, center.Y - 82);
        CenterText(g, "听音位置", font, soft, new RectangleF(center.X - 70, center.Y + 35, 140, 21));
        foreach (var s in Speakers)
        {
            var rect = hit[s.Role]; bool selected = s.Role == SelectedRole;
            bool outputReady = false;
            var device = Devices.FirstOrDefault(d => d.Id == s.DeviceId);
            if (device != null)
            {
                try { SpeakerLayouts.ValidateOutput(s, device); outputReady = true; }
                catch (InvalidOperationException) { }
            }
            bool ready = !s.Muted && s.SourceChannel >= 0 && s.SourceChannel < (Source?.Channels ?? 0) && s.DeviceId != Source?.Id && outputReady && !Speakers.Any(x => x != s && x.DeviceId == s.DeviceId && x.Side == s.Side);
            using var fill = new SolidBrush(selected ? Color.FromArgb(238, 245, 255) : Color.White); using var border = new Pen(selected ? Accent : Color.FromArgb(222, 229, 238), selected ? 2 : 1);
            using var path = Rounded(rect, 11); g.FillPath(fill, path); g.DrawPath(border, path); float cx = rect.X + rect.Width / 2;
            using var speakerInk = new SolidBrush(s.Muted ? Color.FromArgb(170, 181, 195) : selected ? Accent : Color.FromArgb(83, 111, 147));
            bool compact = height < 70;
            float iconWidth = s.Role == "LFE" ? (compact ? 26 : 32) : (compact ? 18 : 22);
            var icon = new RectangleF(cx - iconWidth / 2, rect.Y + (compact ? 4 : 6), iconWidth, compact ? Math.Max(8, height - 42) : Math.Min(28, height - 50));
            using var shape = Rounded(icon, 4); g.FillPath(speakerInk, shape); using var cone = new Pen(Color.FromArgb(240, 247, 255), 1.2f);
            float diameter = compact ? 6 : 10;
            g.DrawEllipse(cone, cx - diameter / 2, icon.Bottom - diameter - 2, diameter, diameter); if (s.Role != "LFE" && icon.Height >= 20) g.DrawEllipse(cone, cx - 2, icon.Y + 3, 4, 4);
            using var dot = new SolidBrush(s.Muted ? Color.FromArgb(179, 187, 197) : ready ? Color.FromArgb(30, 163, 134) : Color.FromArgb(224, 165, 69)); g.FillEllipse(dot, rect.Right - 15, rect.Y + 10, 6, 6);
            CenterText(g, $"{s.Role} · {SpeakerLayouts.Role(s.Role).Name}", compact ? miniStrong : strong, text, new RectangleF(rect.X, rect.Bottom - (compact ? 36 : 44), rect.Width, compact ? 18 : 20));
            CenterText(g, s.Muted ? "静音" : $"{s.GainDb:+0.0;-0.0;0.0} dB", compact ? mini : font, selected ? speakerInk : soft, new RectangleF(rect.X, rect.Bottom - (compact ? 19 : 22), rect.Width, compact ? 17 : 19));
        }
        g.Restore(state);
    }
    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2; var p = new GraphicsPath(); p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
    }
    static void CenterText(Graphics g, string text, Font font, Brush brush, RectangleF rect)
    {
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap }; g.DrawString(UiLanguage.T(text), font, brush, rect, format);
    }
    protected override void Dispose(bool disposing) { if (disposing) tooltip.Dispose(); base.Dispose(disposing); }
}
