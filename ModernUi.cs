using System.Drawing.Drawing2D;
using System.Diagnostics;

namespace ChannelBridge;

public sealed class ModernComboBox : ComboBox
{
    public ModernComboBox() { DrawMode = DrawMode.OwnerDrawFixed; FlatStyle = FlatStyle.Flat; BackColor = Color.FromArgb(248, 250, 253); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ItemHeight = Font.Height + (int)(6 * DeviceDpi / 96f); }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Bounds.Width <= 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var brush = new SolidBrush(selected ? Color.FromArgb(219, 234, 252) : BackColor);
        e.Graphics.FillRectangle(brush, e.Bounds);
        string text = (e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) : Text) ?? "";
        object? item = e.Index >= 0 && e.Index < Items.Count ? Items[e.Index] : null;
        bool deviceName = item is Endpoint || item?.GetType().Name is "DeviceChoice" or "Mic";
        if (!deviceName || text.StartsWith("[") || text.StartsWith("—") || text.StartsWith("演示")) text = UiLanguage.T(text);
        var bounds = Rectangle.Inflate(e.Bounds, -(int)(6 * DeviceDpi / 96f), 0);
        TextRenderer.DrawText(e.Graphics, text, Font, bounds, Enabled ? Color.FromArgb(28, 43, 66) : Color.Gray, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Native combo boxes omit their selected text from WM_PRINT on some Windows themes.
        if ((m.Msg == 0x317 || m.Msg == 0x318) && m.WParam != IntPtr.Zero && SelectedIndex >= 0)
        {
            using var graphics = Graphics.FromHdc(m.WParam);
            var bounds = new Rectangle(2, 2, Math.Max(0, Width - (int)(24 * DeviceDpi / 96f)), Math.Max(0, Height - 4));
            OnDrawItem(new DrawItemEventArgs(graphics, Font, bounds, SelectedIndex, DrawItemState.None));
        }
    }
}

public sealed class ModernButton : Button
{
    bool hover, pressed;
    public ModernButton()
    {
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        BackColor = Color.White; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float radius = 6 * DeviceDpi / 96f, d = radius * 2;
        var r = new RectangleF(1, 1, Width - 3, Height - 3);
        using var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90); path.CloseFigure();
        bool accent = BackColor.GetBrightness() < .7;
        Color fill = !Enabled ? Color.FromArgb(242, 243, 245) : pressed ? (accent ? Color.FromArgb(0, 85, 150) : Color.FromArgb(228, 235, 244)) : hover ? (accent ? Color.FromArgb(0, 105, 185) : Color.FromArgb(242, 246, 251)) : BackColor;
        using var brush = new SolidBrush(fill); e.Graphics.FillPath(brush, path);
        using var pen = new Pen(accent && Enabled ? fill : Color.FromArgb(217, 223, 231)); e.Graphics.DrawPath(pen, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5));
    }
}

public static class FirstRunSetup
{
    public const string CableUrl = "https://vb-audio.com/Cable/";
    public static bool HasCable(IEnumerable<Endpoint> devices) => devices.Any(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
    public static void Run(Form owner, IReadOnlyList<Endpoint> devices)
    {
        string marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChannelBridge", "first-run-complete");
        if (File.Exists(marker) || ServiceFiles.ReadSaved() != null) return;
        if (!HasCable(devices))
        {
            UiMessage.Show(owner, "未检测到默认播放音源 CABLE Input。\n\n请下载并安装 VB-CABLE 虚拟音频驱动。点击确定将打开 VB-Audio 官方下载页面；安装并按提示重启电脑后，回到软件点击“刷新设备”。", "首次使用 · 安装播放音源", MessageBoxButtons.OK, MessageBoxIcon.Information);
            try { Process.Start(new ProcessStartInfo(CableUrl) { UseShellExecute = true }); }
            catch (Exception ex) { UiMessage.Show(owner, "无法打开浏览器，请访问：\n" + CableUrl + "\n" + ex.Message); return; }
        }
        try { Directory.CreateDirectory(Path.GetDirectoryName(marker)!); File.WriteAllText(marker, "1"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
