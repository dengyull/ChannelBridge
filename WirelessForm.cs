using System.Diagnostics;
using System.Text.Json;

namespace ChannelBridge;
public sealed class WirelessForm : Form
{
    readonly bool preview;
    readonly CheckBox enabled = new() { Text = "启用 A2DP Sink（实验性）", AutoSize = true, Margin = new(8) };
    readonly DataGridView devices = new() { Dock = DockStyle.Fill, ReadOnly = true, MultiSelect = false, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    readonly TextBox status = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    readonly Label metrics = new() { Dock = DockStyle.Fill, AutoSize = true };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    readonly List<Button> actionButtons = new();
    WirelessReport? report;
    bool busy;
    public WirelessForm(bool preview = false)
    {
        this.preview = preview;
        SuspendLayout(); AutoScaleMode = AutoScaleMode.None; Font = MainForm.UiFont(14);
        Text = "实验性无线接收"; Size = new(1100, 780); MinimumSize = new(740, 520); StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(243, 246, 250);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, MinimumSize = new(900, 650), Padding = new(18), ColumnCount = 1, RowCount = 6 };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 84)); root.RowStyles.Add(new(SizeType.Absolute, 96));
        root.RowStyles.Add(new(SizeType.Percent, 55)); root.RowStyles.Add(new(SizeType.Absolute, 78));
        root.RowStyles.Add(new(SizeType.Percent, 45)); root.RowStyles.Add(new(SizeType.Absolute, 42));
        root.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Windows 10 2004+ · 接收已配对手机的蓝牙音频。关闭界面不停止接收。\n声音由 Windows 播放；若要进入现有路由，请将系统默认播放设备设为 ChannelBridge 所选音源（通常为 CABLE Input）。蓝牙通常为立体声，不会自动生成环绕声。" }, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        actions.Controls.Add(enabled);
        Button Action(string text, Func<Task> action)
        {
            var button = new ModernButton { Text = text, AutoSize = false, Width = 220, Height = 36, Margin = new(4) };
            button.Click += async (_, _) => { if (busy || preview) return; busy = true; SetBusy(); try { await action(); } catch (Exception ex) { UiMessage.Show(ex.Message, "ChannelBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning); } finally { busy = false; SetBusy(); } };
            actionButtons.Add(button); actions.Controls.Add(button); return button;
        }
        Action("应用开关", () => { bool requested = enabled.Checked; return Save(s => s with { A2dpEnabled = requested }); });
        Action("刷新 / 重试", () => Save(s => s));
        Action("连接并自动恢复", () => {
            string id = SelectedId();
            return Save(s => s with { A2dpEnabled = true, DeviceIds = s.DeviceIds.Append(id).Distinct(StringComparer.Ordinal).ToArray() });
        });
        Action("断开并取消恢复", () => {
            string id = SelectedId(); return Save(s => s with { DeviceIds = s.DeviceIds.Where(x => x != id).ToArray() });
        });
        Action("蓝牙配对设置", () => { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); return Task.CompletedTask; });
        root.Controls.Add(actions, 0, 1);
        foreach (var column in new[] { "蓝牙设备", "连接状态", "尝试", "失败", "断线", "最近错误" }) devices.Columns.Add(column, UiLanguage.T(column));
        devices.Columns[0].FillWeight = 180; devices.Columns[5].FillWeight = 250;
        foreach (int i in new[] { 2, 3, 4 }) devices.Columns[i].FillWeight = 90;
        devices.BackgroundColor = Color.White; devices.BorderStyle = BorderStyle.None;
        root.Controls.Add(devices, 0, 2); root.Controls.Add(metrics, 0, 3); root.Controls.Add(status, 0, 4);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = new ModernButton { Text = "关闭", Width = 90, Height = 34 }; close.Click += (_, _) => Close();
        var export = new ModernButton { Text = "导出诊断报告", Width = 190, Height = 34 }; export.Click += (_, _) => Export();
        var airplay = new ModernButton { Text = "AirPlay 接收设置…", Width = 210, Height = 34 }; airplay.Click += (_, _) => { using var form = new AirPlayForm(preview); form.ShowDialog(this); };
        bottom.Controls.AddRange(new Control[] { close, export, airplay }); root.Controls.Add(bottom, 0, 5);
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; viewport.Controls.Add(root); Controls.Add(viewport);
        UiLanguage.Bind(this);
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new(96, 96); ResumeLayout(true); PerformAutoScale();
        Shown += (_, _) => {
            var area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new(Math.Min(MinimumSize.Width, area.Width - 24), Math.Min(MinimumSize.Height, area.Height - 24));
            Size = new(Math.Min(Width, area.Width - 24), Math.Min(Height, area.Height - 24));
            try { enabled.Checked = !preview && WirelessSettings.Read(ServiceFiles.Root).A2dpEnabled; } catch { }
            RefreshStatus(); if (!preview) timer.Start();
        };
        timer.Tick += (_, _) => RefreshStatus();
        FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); };
    }
    void SetBusy() { enabled.Enabled = !busy; foreach (var b in actionButtons) b.Enabled = !busy; }
    string SelectedId() => devices.SelectedRows.Count == 0 ? throw new IOException("请先选择蓝牙设备。") : (string)devices.SelectedRows[0].Tag!;
    async Task Save(Func<WirelessSettings, WirelessSettings> change)
    {
        // Install/upgrade with the existing elevation flow; do not alter the saved audio routing profile.
        await Task.Run(() => {
            ServiceSetup.EnsureStarted();
            var next = change(WirelessSettings.Read(ServiceFiles.Root)) with { Revision = Guid.NewGuid().ToString("N") };
            next.Validate(); ServiceFiles.AtomicWrite(Path.Combine(ServiceFiles.Root, "wireless.json"), next);
        });
        enabled.Checked = WirelessSettings.Read(ServiceFiles.Root).A2dpEnabled; RefreshStatus();
    }
    public void RefreshStatus()
    {
        try
        {
            report = preview ? new("preview", false, "Disabled", "", DateTime.UtcNow, 0, 0, 0, 0, 0, 0, 0,
                new[] { new WirelessDeviceStatus("preview-device", "Demo phone", true, false, "Available", 0, 0, 0, "", null) }, Array.Empty<string>())
                : JsonSerializer.Deserialize<WirelessReport>(File.ReadAllText(Path.Combine(ServiceFiles.Root, "wireless-status.json")));
            if (report == null) throw new IOException("尚无无线接收状态。");
            string selected = devices.SelectedRows.Count == 0 ? "" : (string)devices.SelectedRows[0].Tag!;
            devices.Rows.Clear();
            foreach (var d in report.Devices)
            {
                int i = devices.Rows.Add(d.Name, UiLanguage.T(StateName(d.State)), d.Attempts, d.Failures, d.Disconnects, UiLanguage.T(d.Error));
                devices.Rows[i].Tag = d.Id; if (d.Id == selected) devices.Rows[i].Selected = true;
            }
            bool stale = DateTime.UtcNow - report.UpdatedUtc > TimeSpan.FromSeconds(10);
            metrics.Text = UiLanguage.T(stale ? "服务状态已过期；请检查服务是否运行。" : "服务状态：") + (stale ? "" : UiLanguage.T(StateName(report.State))) +
                $"\r\nCPU {report.CpuPercent:F2}%  |  avg {report.AverageCpuPercent:F2}%  |  peak {report.PeakCpuPercent:F2}%  |  RAM {report.WorkingSetBytes / 1048576.0:F1} MB  |  handles {report.Handles}  |  uptime {report.UptimeSeconds:F0}s" +
                "\r\n" + UiLanguage.T("CPU 为整个服务占整机比例；不含 Windows 蓝牙解码进程。计数自本次连接任务开始。实际播放稳定性尚待实测。");
            status.Text = UiLanguage.T("AirPlay：通过下方按钮配置独立接收引擎。Chromecast：暂不支持通用接收，存在设备认证限制。\r\nA2DP：首次配对请使用 Windows 设置；服务会话兼容性需实机确认。被系统拒绝时不会反复重连，请点击刷新 / 重试。")
                + "\r\n" + UiLanguage.T(report.Error) + "\r\n" + string.Join("\r\n", report.Events.TakeLast(12));
        }
        catch (Exception ex)
        {
            devices.Rows.Clear(); metrics.Text = UiLanguage.T("尚无无线接收状态。") + " " + ex.Message;
            status.Text = UiLanguage.T("功能默认关闭。选择启用后点击“应用开关”，由后台服务开始发现已配对的设备。AirPlay 需要单独安装接收引擎；Chromecast 尚不可用。");
        }
    }
    static string StateName(string value) => value switch {
        "Disabled" => "已关闭", "Ready" => "已就绪", "Available" => "可连接", "Waiting" => "等待设备", "Connecting" => "连接中",
        "Connected" => "已连接", "Retrying" => "等待重试", "Denied" => "需要重试 / 检查系统限制", "Error" => "错误", _ => value };
    void Export()
    {
        using var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "ChannelBridge-wireless-diagnostics.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (report == null) throw new IOException("尚无无线接收状态。");
            // Export device display names and metrics, not opaque Bluetooth identifiers or audio content.
            var sanitized = report with { Devices = report.Devices.Select(d => d with { Id = "" }).ToArray() };
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { Note = "Experimental. Service CPU excludes OS Bluetooth decoding. No audio recorded.", Report = sanitized }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { UiMessage.Show(ex.Message, "ChannelBridge"); }
    }
}
