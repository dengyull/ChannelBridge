using System.Runtime.InteropServices;
namespace ChannelBridge;
public class LanguageForm : Form
{
    readonly Panel caption = new() { Height = 34, Dock = DockStyle.Top, BackColor = Color.FromArgb(235, 240, 247) };
    readonly Label title = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new(12,0,0,0) };
    readonly Button language = new ModernButton { Text = "文 / A", Width = 66, Dock = DockStyle.Right, AccessibleName = "Switch language / 切换语言" };
    readonly ToolTip hint = new();
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    public LanguageForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        caption.Controls.Add(title);
        foreach (var (text, action) in new (string, Action)[] { ("—", () => WindowState = FormWindowState.Minimized), ("□", Maximize), ("×", Close) })
        {
            var button = new ModernButton { Text = text, Width = 46, Dock = DockStyle.Right, AccessibleName = text == "—" ? "Minimize" : text == "□" ? "Maximize / Restore" : "Close" };
            button.Click += (_, _) => action(); caption.Controls.Add(button); button.BringToFront();
        }
        caption.Controls.Add(language); language.BringToFront();
        // Dock right in this order: language, minimize, maximize, close.
        var buttons = caption.Controls.OfType<Button>().ToArray();
        caption.Controls.SetChildIndex(title, 0);
        int order = 1;
        foreach (string text in new[] { "文 / A", "—", "□", "×" }) caption.Controls.SetChildIndex(buttons.Single(b => b.Text == text), order++);
        Controls.Add(caption);
        title.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } };
        title.DoubleClick += (_, _) => Maximize();
        language.Click += (_, _) => UiLanguage.Set(!UiLanguage.English);
        hint.SetToolTip(language, "中文 / English");
        TextChanged += (_, _) => title.Text = Text;
        UiLanguage.Changed += RefreshLanguage;
        FormClosed += (_, _) => { UiLanguage.Changed -= RefreshLanguage; hint.Dispose(); };
    }
    void Maximize() { MaximizedBounds = Screen.FromControl(this).WorkingArea; WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; }
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e); caption.SendToBack(); UiLanguage.Bind(this); RefreshLanguage();
    }
    void RefreshLanguage()
    {
        UiLanguage.Apply(this); language.Text = UiLanguage.English ? "中 / EN" : "文 / A";
        title.Text = Text; OnLanguageChanged(); Invalidate(true);
    }
    protected virtual void OnLanguageChanged() { }
    protected Button AddCaptionAction(string text, Action action)
    {
        var button = new ModernButton { Text = text, Width = 112, Dock = DockStyle.Right };
        button.Click += (_, _) => action();
        caption.Controls.Add(button); caption.Controls.SetChildIndex(button, 1);
        return button;
    }
    protected void VerifyCaption()
    {
        var minimize = caption.Controls.OfType<Button>().Single(b => b.Text == "—");
        if (!caption.Visible || caption.Top != 0 || language.Right != minimize.Left || language.Width <= 0) throw new Exception("Language button must be next to minimize in the title bar.");
        foreach (Control child in Controls) if (child != caption && child.Top < caption.Bottom) throw new Exception("Title bar overlaps content.");
    }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x84 && WindowState == FormWindowState.Normal)
        {
            long point = m.LParam.ToInt64(); var p = PointToClient(new Point((short)(point & 0xffff), (short)((point >> 16) & 0xffff)));
            int edge = Math.Max(5, (int)(6 * DeviceDpi / 96f));
            bool l = p.X < edge, r = p.X >= Width-edge, t = p.Y < edge, b = p.Y >= Height-edge;
            int hit = t ? l ? 13 : r ? 14 : 12 : b ? l ? 16 : r ? 17 : 15 : l ? 10 : r ? 11 : 0;
            if (hit != 0) m.Result = (IntPtr)hit;
        }
    }
}
