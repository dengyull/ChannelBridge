using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChannelBridge;
public sealed class AirPlayForm : Form
{
    readonly bool preview;
    readonly CheckBox enabled = new() { Text = "启用 AirPlay（实验性）", AutoSize = true };
    readonly TextBox engine = new() { Dock = DockStyle.Fill, ReadOnly = true };
    readonly TextBox name = new() { Dock = DockStyle.Fill, Text = "ChannelBridge", MaxLength = 63 };
    readonly TextBox status = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    readonly Button apply = new ModernButton { Text = "应用 / 重试", Width = 150, Height = 34 };
    AirPlayReport? report;
    public AirPlayForm(bool preview = false)
    {
        this.preview = preview;
        SuspendLayout(); AutoScaleMode = AutoScaleMode.None; Font = MainForm.UiFont(14);
        Text = "AirPlay 实验性接收"; Size = new(980, 650); MinimumSize = new(720, 500); StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(243, 246, 250);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, MinimumSize = new(900, 570), Padding = new(18), ColumnCount = 3, RowCount = 7 };
        root.ColumnStyles.Add(new(SizeType.Absolute, 145)); root.ColumnStyles.Add(new(SizeType.Percent, 100)); root.ColumnStyles.Add(new(SizeType.Absolute, 130));
        foreach (int height in new[] { 104, 40, 44, 44, 42 }) root.RowStyles.Add(new(SizeType.Absolute, height));
        root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 42));
        var note = new Label { Dock = DockStyle.Fill, Text = "需要另行安装完整的 UxPlayEnhanced 接收引擎。选择包内的 uxplay.exe，由服务管理独立进程；默认关闭，不启用视频。\n声音通过 WASAPI 输出到已应用预设的音源（通常为 CABLE Input）；请按引擎说明配置私有网络防火墙。进程运行不代表已成功连接手机，兼容性和播放稳定性尚待实测。" };
        root.Controls.Add(note, 0, 0); root.SetColumnSpan(note, 3);
        root.Controls.Add(enabled, 0, 1); root.SetColumnSpan(enabled, 3);
        root.Controls.Add(new Label { Text = "接收引擎", Dock = DockStyle.Fill }, 0, 2); root.Controls.Add(engine, 1, 2);
        var browse = new ModernButton { Text = "选择…", Dock = DockStyle.Fill }; browse.Click += (_, _) => {
            using var dialog = new OpenFileDialog { Filter = "UxPlayEnhanced|uxplay.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) engine.Text = dialog.FileName;
        }; root.Controls.Add(browse, 2, 2);
        root.Controls.Add(new Label { Text = "接收名称", Dock = DockStyle.Fill }, 0, 3); root.Controls.Add(name, 1, 3); root.SetColumnSpan(name, 2);
        var download = new ModernButton { Text = "打开接收引擎下载页", Dock = DockStyle.Fill };
        download.Click += (_, _) => { if (!preview) Process.Start(new ProcessStartInfo("https://github.com/Kylepossible/UxPlayEnhanced/releases") { UseShellExecute = true }); };
        root.Controls.Add(download, 0, 4); root.SetColumnSpan(download, 3);
        root.Controls.Add(status, 0, 5); root.SetColumnSpan(status, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = new ModernButton { Text = "关闭", Width = 90, Height = 34 }; close.Click += (_, _) => Close();
        var export = new ModernButton { Text = "导出诊断报告", Width = 190, Height = 34 }; export.Click += (_, _) => Export();
        buttons.Controls.AddRange(new Control[] { close, apply, export }); root.Controls.Add(buttons, 0, 6); root.SetColumnSpan(buttons, 3);
        apply.Click += async (_, _) => {
            if (preview) return;
            var desired = new AirPlaySettings(enabled.Checked, engine.Text, "", name.Text, Guid.NewGuid().ToString("N"));
            apply.Enabled = enabled.Enabled = browse.Enabled = name.Enabled = false;
            try
            {
                await Task.Run(() => {
                    if (desired.Enabled) { using var file = File.OpenRead(desired.EnginePath); desired = desired with { EngineSha256 = Convert.ToHexString(SHA256.HashData(file)) }; }
                    desired.Validate(desired.Enabled); ServiceSetup.EnsureStarted(); ServiceFiles.AtomicWrite(Path.Combine(ServiceFiles.Root, "airplay.json"), desired);
                }); RefreshStatus();
            }
            catch (Exception ex) { UiMessage.Show(ex.Message, "ChannelBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { apply.Enabled = enabled.Enabled = browse.Enabled = name.Enabled = true; }
        };
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; viewport.Controls.Add(root); Controls.Add(viewport); UiLanguage.Bind(this);
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new(96, 96); ResumeLayout(true); PerformAutoScale();
        Shown += (_, _) => {
            var area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new(Math.Min(MinimumSize.Width, area.Width - 24), Math.Min(MinimumSize.Height, area.Height - 24));
            Size = new(Math.Min(Width, area.Width - 24), Math.Min(Height, area.Height - 24));
            try { var config = preview ? AirPlaySettings.Disabled : AirPlaySettings.Read(ServiceFiles.Root); enabled.Checked = config.Enabled; engine.Text = config.EnginePath; name.Text = config.ReceiverName; } catch { }
            RefreshStatus(); if (!preview) timer.Start();
        };
        timer.Tick += (_, _) => RefreshStatus(); FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); };
    }
    void RefreshStatus()
    {
        try
        {
            report = preview ? new("preview", "Disabled", "", DateTime.UtcNow, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<string>())
                : JsonSerializer.Deserialize<AirPlayReport>(File.ReadAllText(Path.Combine(ServiceFiles.Root, "airplay-status.json")));
            if (report == null) throw new IOException("尚无 AirPlay 服务状态。");
            string state = DateTime.UtcNow - report.UpdatedUtc > TimeSpan.FromSeconds(10) ? "服务状态已过期；请检查服务是否运行。"
                : report.State switch { "Disabled" => "已关闭", "Running" => "引擎运行中（播放未验证）", "Error" => "错误", _ => "等待重试" };
            status.Text = UiLanguage.T(state) + $"\r\nPID {report.ProcessId}  |  CPU {report.CpuPercent:F2}%  |  avg {report.AverageCpuPercent:F2}%  |  peak {report.PeakCpuPercent:F2}%\r\nRAM {report.WorkingSetBytes / 1048576.0:F1} MB  |  uptime {report.UptimeSeconds:F0}s  |  starts {report.Starts}  |  unexpected exits {report.UnexpectedExits}\r\n"
                + UiLanguage.T("此处 CPU 仅统计 AirPlay 接收进程，占整机比例；不含 ChannelBridge 服务。连续失败五次后暂停，需手动应用 / 重试。")
                + "\r\n" + UiLanguage.T(report.Error) + "\r\n" + string.Join("\r\n", report.Events.TakeLast(16));
        }
        catch (Exception ex) { status.Text = UiLanguage.T("功能默认关闭，尚未收到 AirPlay 服务状态。") + "\r\n" + ex.Message; }
    }
    void Export()
    {
        using var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "ChannelBridge-airplay-diagnostics.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { UiMessage.Show(ex.Message, "ChannelBridge"); }
    }
}
