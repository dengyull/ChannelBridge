using System.Text.Json;
using NAudio.CoreAudioApi;

namespace ChannelBridge;
public sealed class CalibrationForm : Form
{
    readonly SurroundProfile draft;
    readonly ComboBox microphones = new ModernComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 430 };
    readonly Button start = new ModernButton { Text = "应用并开始测量", AutoSize = true, Padding = new(12, 6, 12, 6), BackColor = Color.FromArgb(0, 103, 192), ForeColor = Color.White };
    readonly Button cancel = new ModernButton { Text = "停止测量", AutoSize = true, Padding = new(12, 6, 12, 6), Enabled = false };
    readonly Button close = new ModernButton { Text = "关闭", AutoSize = true, Padding = new(12, 6, 12, 6) };
    readonly TextBox status = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None };
    readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    string runId = "";
    bool active, preparing, closeWhenDone;
    int servicePid;
    DateTime started;
    public CalibrationForm(SurroundProfile draft, bool preview = false)
    {
        this.draft = draft;
        SuspendLayout(); AutoScaleMode = AutoScaleMode.None; Font = MainForm.UiFont(14);
        BackColor = Color.FromArgb(243, 246, 250); status.BackColor = BackColor;
        microphones.FlatStyle = FlatStyle.Flat; microphones.BackColor = Color.White;
        grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(232, 238, 246);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(28, 43, 66);
        grid.GridColor = Color.FromArgb(231, 235, 242); grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 249, 252);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 252); grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(28, 43, 66);
        Text = "麦克风延迟测量 · 三次中位数与补偿复测"; Size = new(1260, 740); StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new(16) };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 110));
        var help = new Label { AutoSize = true, Dock = DockStyle.Fill, Text = "将麦克风固定在听音位置，暂停其他声音，关闭麦克风回放/监听并保持音量不变。\n每只音箱初测 3 次取中位数，以最慢者为基准补偿，再复测 3 次。仅全部通过验证才自动保存。\n测量使用短扫频声（低音炮为低频扫频）；原延迟暂时归零。单只音箱三次极差须 ≤ 10 ms；各音箱复测均值及中位数极差须 ≤ 5 ms。" };
        root.Controls.Add(help, 0, 0);
        var tools = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Padding = new(0, 12, 0, 12) };
        tools.Controls.AddRange(new Control[] { new Label { Text = "测量麦克风", AutoSize = true, Padding = new(0, 7, 0, 0) }, microphones, start, cancel, close }); root.Controls.Add(tools, 0, 1);
        string[] headings = { "音箱", "初测 1", "初测 2", "初测 3", "中位数", "平均值", "初测极差", "补偿 ms", "复测 1", "复测 2", "复测 3", "复测中位数", "复测均值", "复测极差", "验证" };
        for (int i = 0; i < headings.Length; i++) grid.Columns.Add("c" + i, headings[i]);
        grid.DefaultCellStyle.Format = "0.00"; grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        root.Controls.Add(grid, 0, 2); root.Controls.Add(status, 0, 3); Controls.Add(root);
        status.Text = "仅在点击开始后采集麦克风；音频只在内存中分析，不保存录音。测量值包含声音传播和设备延迟，主要用于各音箱的相对对齐。";
        start.Click += async (_, _) => await StartMeasurement(); cancel.Click += (_, _) => CancelMeasurement(); timer.Tick += (_, _) => Poll();
        close.Click += (_, _) => Close();
        FormClosing += (_, e) => { if (active) { e.Cancel = true; closeWhenDone = true; CancelMeasurement(); } };
        FormClosed += (_, _) => timer.Dispose();
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new(96, 96); ResumeLayout(true); PerformAutoScale();
        Shown += (_, _) =>
        {
            UiLanguage.Bind(this);
            foreach (DataGridViewColumn column in grid.Columns) column.HeaderText = UiLanguage.T(column.HeaderText);
            var area = Screen.FromControl(this).WorkingArea; Size = new(Math.Min(Width, area.Width - 24), Math.Min(Height, area.Height - 24));
            if (preview) { start.Enabled = false; ShowResult(new("preview", true, false, "演示数据 · 非实际测量结果", LatencyChecks.Example())); return; }
            try
            {
            using var e = new MMDeviceEnumerator(); string defaultId = "";
            try { using var d = e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); defaultId = d.ID; } catch { }
            foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)) using (d) microphones.Items.Add(new Mic(d.ID, d.FriendlyName));
            microphones.SelectedItem = microphones.Items.Cast<Mic>().FirstOrDefault(m => m.Id == defaultId) ?? microphones.Items.Cast<Mic>().FirstOrDefault();
            if (microphones.Items.Count == 0) { start.Enabled = false; status.Text = "未发现可用麦克风。连接麦克风后重新打开此窗口。"; }
            }
            catch (Exception ex) { start.Enabled = false; status.Text = "无法列出麦克风：" + ex.Message; }
        };
    }
    async Task StartMeasurement()
    {
        if (active || preparing || microphones.SelectedItem is not Mic mic) return;
        preparing = true; start.Enabled = microphones.Enabled = false; grid.Rows.Clear(); status.Text = "正在应用配置并准备后台测量；必要时会请求管理员授权…";
        try
        {
            var snapshot = SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(draft), AudioEngine.Devices());
            await Task.Run(() => ProfileApplication.Apply(new ApplyRequest(snapshot, null)));
            if (IsDisposed) return;
            var saved = ServiceFiles.ReadSaved() ?? throw new IOException("无法读取保存的配置。");
            runId = Guid.NewGuid().ToString("N"); servicePid = 0; started = DateTime.UtcNow;
            ServiceFiles.AtomicWrite(Path.Combine(ServiceFiles.Root, "calibration-request.json"), new CalibrationCommand(runId, runId, DateTime.UtcNow.AddSeconds(30), mic.Id, saved.Revision, saved.Profile));
            active = true; cancel.Enabled = true; timer.Start(); status.Text = "等待后台开始。请保持安静和麦克风位置不变。";
        }
        catch (Exception ex) { if (!IsDisposed) status.Text = "无法开始：" + ex.Message; }
        finally { preparing = false; if (!IsDisposed && !active) start.Enabled = microphones.Enabled = true; }
    }
    void CancelMeasurement()
    {
        if (!active) return;
        try
        {
            ServiceFiles.AtomicWrite(Path.Combine(ServiceFiles.Root, "calibration-request.json"), new CalibrationCommand(Guid.NewGuid().ToString("N"), runId, DateTime.UtcNow.AddSeconds(30), "", "", null, true));
            cancel.Enabled = false; status.Text = "正在停止录音与测试声，并恢复正常路由…";
        }
        catch (Exception ex) { closeWhenDone = false; UiMessage.Show(this, "停止请求失败：" + ex.Message); }
    }
    void Poll()
    {
        try
        {
            string path = Path.Combine(ServiceFiles.Root, "calibration-result.json");
            if (File.Exists(path))
            {
                var result = JsonSerializer.Deserialize<CalibrationReport>(File.ReadAllText(path));
                if (result?.Id == runId)
                {
                    ShowResult(result); Finish();
                    if (!IsDisposed && result.Success && result.Applied && UiMessage.Show(this, "延迟测试及复测已通过，补偿已自动保存。\n\n是否关闭结果窗口？选择“否”可继续查看三次结果对比。", "延迟测试成功", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) Close();
                    return;
                }
            }
            var report = JsonSerializer.Deserialize<ServiceReport>(File.ReadAllText(Path.Combine(ServiceFiles.Root, "status.json")));
            if (report == null) return;
            if (servicePid == 0) servicePid = report.ProcessId;
            if (report.ProcessId != servicePid) { status.Text = "服务已重启，本次测量中断，未取得完整验证结果。请重新测量。"; Finish(); return; }
            if (report.State == "Calibrating") status.Text = report.Message + "\r\n不要移动麦克风或调整音量；如需退出，请点击停止测量。";
            else if (DateTime.UtcNow - started > TimeSpan.FromSeconds(15)) { status.Text = "后台未返回测量结果，请检查麦克风权限或服务状态后重试。"; Finish(); }
        }
        catch (IOException) { }
        catch (Exception ex) { status.Text = "读取测量状态失败：" + ex.Message; }
    }
    void Finish() { active = false; timer.Stop(); start.Enabled = microphones.Enabled = true; cancel.Enabled = false; if (closeWhenDone) Close(); }
    void ShowResult(CalibrationReport result)
    {
        grid.Rows.Clear();
        static string Number(double? n) => n?.ToString("0.00") ?? "—";
        static string Trial(List<LatencyTrial> trials, int i) => i >= trials.Count ? "—" : trials[i].Milliseconds?.ToString("0.00") ?? "无效";
        foreach (var row in result.Rows)
        {
            int index = grid.Rows.Add(row.Role + " " + SpeakerLayouts.Role(row.Role).Name, Trial(row.Initial, 0), Trial(row.Initial, 1), Trial(row.Initial, 2), Number(row.Median), Number(row.Mean), Number(row.Spread), row.Median == null ? "—" : row.CompensationMs.ToString(), Trial(row.Verified, 0), Trial(row.Verified, 1), Trial(row.Verified, 2), Number(row.VerifiedMedian), Number(row.VerifiedMean), Number(LatencyAnalysis.Spread(row.Verified)), result.Success ? "通过" : "未通过");
            for (int i = 0; i < row.Initial.Count; i++) grid.Rows[index].Cells[1 + i].ToolTipText = $"匹配度 {row.Initial[i].Confidence:0.00} {row.Initial[i].Error}";
            for (int i = 0; i < row.Verified.Count; i++) grid.Rows[index].Cells[8 + i].ToolTipText = $"匹配度 {row.Verified[i].Confidence:0.00} {row.Verified[i].Error}";
            foreach (DataGridViewCell cell in grid.Rows[index].Cells) { if (cell.Value is string value) cell.Value = UiLanguage.T(value); cell.ToolTipText = UiLanguage.T(cell.ToolTipText); }
        }
        status.Text = result.Message + "\r\n所有数值单位为 ms。悬停在单次测量格上可查看匹配度或失败原因。";
        status.ForeColor = result.Success ? Color.DarkGreen : Color.Firebrick;
    }
    sealed record Mic(string Id, string Name) { public override string ToString() => Name; }
}
