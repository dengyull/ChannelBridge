using System.Diagnostics;
using System.Text.Json;

namespace ChannelBridge;

public sealed class MainForm : LanguageForm
{
    static readonly Color Ink = Color.FromArgb(28, 43, 66), MutedInk = Color.FromArgb(103, 117, 137), Blue = Color.FromArgb(44, 108, 224);
    readonly ComboBox source = Combo(480), layout = Combo(280), speakerSelect = Combo(300), destination = Combo(300), sourceChannel = Combo(300), outputSide = Combo(300);
    readonly NumericUpDown gain = new() { DecimalPlaces = 1, Increment = .5m, Minimum = -60, Maximum = 6, Width = 136 };
    readonly NumericUpDown delay = new() { Maximum = 500, Width = 136 };
    readonly DeviceVolumeControl deviceVolume = new();
    readonly CheckBox nativeOutput = new() { Text = "使用设备多声道", AutoSize = true };
    readonly CheckBox lowLatency = new() { Text = "低延迟（缓冲 50ms）", AutoSize = true, Margin = new(6, 8, 8, 3) };
    readonly ToolTip latencyTip = new();
    BufferSettings buffers = BufferSettings.Standard;
    string servicePriority = "Normal";
    readonly ContextMenuStrip advancedMenu = new();
    readonly Button advanced;
    readonly Label info = Label("", 13), status = Label("", 13), summary = Label("", 13), detail = Label("", 13), speakerTitle = Label("", 24, true);
    readonly SpeakerMap map = new() { Dock = DockStyle.Fill };
    readonly Panel viewport = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly TableLayoutPanel page = new() { Padding = new(24, 18, 24, 12), Margin = Padding.Empty, ColumnCount = 1, RowCount = 6 };
    readonly TableLayoutPanel body = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
    readonly TableLayoutPanel left = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new(0, 0, 18, 0), BackColor = Color.White };
    readonly Panel inspector = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, Margin = Padding.Empty };
    readonly FlowLayoutPanel actions = new() { Dock = DockStyle.Fill, WrapContents = false, Padding = new(0, 10, 0, 0), Margin = Padding.Empty };
    readonly List<(Control Control, int X, int Y, int Width)> inspectorSpec = new();
    bool fittingPage, fittingInspector;
    readonly Button run, stop, test, testAll, autoMatch, volumeFollow, refresh, save, load;
    readonly Button systemSound;
    readonly Button quickMap;
    bool automaticSource = true, followVolume = true;
    DateTime nextSourceRefresh;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    List<Endpoint> devices = new();
    List<SpeakerSetting> speakers = new();
    bool preview, serviceBusy;
    ServiceReport? serviceReport;


    bool loading;
    string selectedRole = "FL";
    SpeakerLayout CurrentLayout => layout.SelectedItem as SpeakerLayout ?? SpeakerLayouts.Get("5.1");
    Endpoint? Source => source.SelectedItem as Endpoint;
    SpeakerSetting? Selected => speakers.FirstOrDefault(s => s.Role == selectedRole);
    public MainForm()
    {
        SuspendLayout(); AutoScaleMode = AutoScaleMode.None; Font = UiFont(14); ForeColor = Ink;
        Text = "ChannelBridge · 多设备环绕声"; BackColor = Color.FromArgb(243, 246, 250);
        Size = new(1440, 990); MinimumSize = new(900, 650); StartPosition = FormStartPosition.CenterScreen;
        var root = page; viewport.Controls.Add(page); Controls.Add(viewport);
        page.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 48)); root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Absolute, 42));
        root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 57)); root.RowStyles.Add(new(SizeType.Absolute, 27));
        var header = new Panel { Dock = DockStyle.Fill };
        systemSound = Button("系统声音设置", () => Open("mmsys.cpl"));
        advanced = AddCaptionAction("高级设置 ▾", ShowAdvancedMenu);
        systemSound.Dock = DockStyle.Right; systemSound.Width = 152;
        header.Controls.Add(systemSound);
        header.Controls.Add(new Label { Text = "ChannelBridge", AutoSize = true, Font = UiFont(28, true), Location = new(0, 0), ForeColor = Ink });
        header.Controls.Add(new Label { Text = "环绕声映射  /  2.0 — 7.1", AutoSize = true, Font = UiFont(14), ForeColor = MutedInk, Location = new(234, 12) }); root.Controls.Add(header, 0, 0);
        layout.Items.AddRange(SpeakerLayouts.All); layout.SelectedItem = SpeakerLayouts.Get("5.1");
        refresh = Button("刷新设备", RefreshDevices); autoMatch = Button("自动匹配：开", () => { automaticSource = !automaticSource; if (automaticSource) MatchSource(); Render(); FitPage(); });
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        top.Controls.AddRange(new Control[] { Caption("播放音源"), source, Caption("声道配置"), layout, refresh, autoMatch, lowLatency }); root.Controls.Add(top, 0, 1);
        lowLatency.CheckedChanged += (_, _) => { if (!loading) { buffers = lowLatency.Checked ? BufferSettings.LowLatency : BufferSettings.Standard; RefreshBufferLabel(); status.Text = "缓冲设置将在应用配置后生效；切换模式后建议重新测量延迟补偿。"; } };
        latencyTip.SetToolTip(lowLatency, UiLanguage.T("关闭时使用标准缓冲。50ms 是转发缓冲目标；高级设置可分别调整捕获、转发和输出缓冲。"));
        info.Dock = DockStyle.Fill; info.TextAlign = ContentAlignment.MiddleLeft; info.ForeColor = MutedInk; root.Controls.Add(info, 0, 2);
        body.ColumnStyles.Add(new(SizeType.Percent, 100)); body.ColumnStyles.Add(new(SizeType.Absolute, 376)); root.Controls.Add(body, 0, 3);
        left.RowStyles.Add(new(SizeType.Percent, 100)); left.RowStyles.Add(new(SizeType.Absolute, 67)); left.Controls.Add(map, 0, 0);
        left.ColumnStyles.Add(new(SizeType.Percent, 100));
        summary.Dock = DockStyle.Fill; summary.Padding = new(20, 10, 12, 8); summary.ForeColor = MutedInk; left.Controls.Add(summary, 0, 1); body.Controls.Add(left, 0, 0);
        body.Controls.Add(inspector, 1, 0);
        void Place(Control c, int x, int y, int width = 0) { c.Location = new(x, y); if (width > 0) c.Width = width; inspector.Controls.Add(c); inspectorSpec.Add((c, x, y, width)); }
        Place(Label("音箱属性", 13), 22, 20); Place(speakerSelect, 22, 49, 310); Place(speakerTitle, 22, 90);
        Place(Caption("音源声道"), 18, 136); Place(sourceChannel, 22, 164, 310);
        Place(Caption("输出设备"), 18, 210); Place(destination, 22, 238, 310);
        Place(Caption("输出接口"), 18, 284); outputSide.Items.AddRange(new object[] { "L · 设备左声道", "R · 设备右声道" }); outputSide.SelectedIndex = 0; Place(outputSide, 22, 312, 310);
        Place(Caption("增益 / dB"), 18, 358); Place(Caption("延迟补偿 / ms"), 185, 358); Place(gain, 22, 386); Place(delay, 189, 386); Place(deviceVolume, 22, 435, 310);
        test = Button("▶ 播放测试音频", () => RequestTest(false)); Place(test, 22, 473, 310);
        detail.AutoSize = false; detail.Size = new(310, 96); detail.ForeColor = MutedInk; Place(detail, 22, 527);
        Place(nativeOutput, 22, 277);
        quickMap = Button("快速映射…", QuickMapDevice); Place(quickMap, 22, 313, 310);
        run = Button("▶ 启动服务 / 路由", Start); run.BackColor = Blue; run.ForeColor = Color.White; run.FlatAppearance.BorderSize = 0;
        stop = Button("■ 停止路由", Stop); stop.Enabled = false; save = Button("保存配置…", SaveProfile); load = Button("载入配置…", LoadProfile);
        testAll = Button("▶ 测试配置", () => RequestTest(true));
        volumeFollow = Button("系统音量：跟随", ToggleVolumeFollow);
        actions.WrapContents = true;
        actions.Controls.AddRange(new Control[] { run, stop, Button("应用配置", ApplyConfiguration), testAll, save, load, Button("重启服务", RestartService), Button("使用说明", () => Open(Path.Combine(AppContext.BaseDirectory, UiLanguage.English ? "User guide.md" : "使用说明.md"))), volumeFollow, Button("麦克风测延迟", MeasureLatency) });
        root.Controls.Add(actions, 0, 4); status.Dock = DockStyle.Fill; root.Controls.Add(status, 0, 5);
        info.AutoSize = summary.AutoSize = status.AutoSize = false;
        layout.SelectedIndexChanged += (_, _) => { if (!loading) ChangeLayout(); };
        source.SelectedIndexChanged += (_, _) => { if (!loading) MatchSource(); };
        speakerSelect.SelectedIndexChanged += (_, _) => { if (!loading && speakerSelect.SelectedItem is SpeakerChoice c) SelectSpeaker(c.Id); };
        map.SpeakerSelected += SelectSpeaker;
        sourceChannel.SelectedIndexChanged += (_, _) => EditSelected(); destination.SelectedIndexChanged += (_, _) => ChangeDestination(); outputSide.SelectedIndexChanged += (_, _) => EditSelected();
        nativeOutput.CheckedChanged += (_, _) => ChangeOutputMode();
        gain.ValueChanged += (_, _) => EditSelected(); delay.ValueChanged += (_, _) => EditSelected();
        timer.Tick += (_, _) => UpdateMeters(); timer.Start();
        FormClosing += (_, _) => { timer.Dispose(); latencyTip.Dispose(); advancedMenu.Dispose(); };
        speakers = SpeakerLayouts.Create(CurrentLayout, null); RefreshDevices(); Render();
        // One 96-DPI baseline for native controls. Point fonts follow monitor DPI;
        // the custom map uses the same logical coordinate system independently.
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        ResumeLayout(true); PerformAutoScale();
        viewport.ClientSizeChanged += (_, _) => FitPage();
        inspector.ClientSizeChanged += (_, _) => { if (!fittingPage) LayoutInspector(page.Width < Px(1180) || page.Height < Px(840)); };
        Shown += (_, _) => { FitWindowToScreen(); FitPage(); if (!preview) { RestoreSaved(); FirstRunSetup.Run(this, devices); } };
        DpiChanged += (_, _) => BeginInvoke((Action)(() => { FitWindowToScreen(); FitPage(); map.Invalidate(); }));
        FitPage();
    }
    public static Font UiFont(float size, bool bold = false) => new("Microsoft YaHei UI", size * 72 / 96, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
    void RefreshBufferLabel() => lowLatency.Text = buffers == BufferSettings.Standard || buffers == BufferSettings.LowLatency
        ? "低延迟（缓冲 50ms）" : "自定义缓冲（高级设置）";
    void ShowAdvancedMenu()
    {
        advancedMenu.Items.Clear();
        var routeMute = new ToolStripMenuItem(UiLanguage.T("仅静音选中音箱（应用后生效）")) { Checked = Selected?.Muted == true, Enabled = Selected != null };
        routeMute.Click += (_, _) => { if (Selected is { } s) { s.Muted = !s.Muted; Render(); } };
        advancedMenu.Items.Add(routeMute);
        advancedMenu.Items.Add(UiLanguage.T("音频缓冲…"), null, (_, _) => EditBuffers());
        var priorityMenu = new ToolStripMenuItem(UiLanguage.T("程序优先级（后台服务）"));
        foreach (var option in ServicePriority.Options)
        {
            var item = new ToolStripMenuItem(UiLanguage.T(option.Label)) { Checked = servicePriority == option.Id };
            item.Click += (_, _) => {
                servicePriority = option.Id;
                status.Text = "后台服务优先级将在应用配置后生效，并在服务重启后保留。";
            };
            priorityMenu.DropDownItems.Add(item);
        }
        advancedMenu.Items.Add(priorityMenu);
        advancedMenu.Items.Add(UiLanguage.T("实验性无线接收…"), null, (_, _) => { using var dialog = new WirelessForm(preview); dialog.ShowDialog(this); });
        advancedMenu.Show(advanced, new Point(0, advanced.Height));
    }
    void EditBuffers()
    {
        using var dialog = new BufferSettingsForm(buffers);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        buffers = dialog.Settings;
        bool prior = loading; loading = true;
        try { lowLatency.Checked = buffers != BufferSettings.Standard; }
        finally { loading = prior; }
        RefreshBufferLabel();
        status.Text = "缓冲设置将在应用配置后生效；切换模式后建议重新测量延迟补偿。";
    }
    int Px(int logical) => (int)Math.Round(logical * DeviceDpi / 96f);
    protected override void OnLanguageChanged() { if (source == null || page == null || run == null) return; latencyTip.SetToolTip(lowLatency, UiLanguage.T("关闭时使用标准缓冲。50ms 是转发缓冲目标；高级设置可分别调整捕获、转发和输出缓冲。")); FitPage(); map.Invalidate(); }
    void FitPage()
    {
        if (fittingPage || IsDisposed) return;
        fittingPage = true;
        try
        {
            page.SuspendLayout();
            page.Size = new(Math.Max(Px(860), viewport.ClientSize.Width), Math.Max(Px(UiLanguage.English ? 780 : 650), viewport.ClientSize.Height));
            bool compact = page.Width < Px(1180) || page.Height < Px(840);
            page.Padding = new(Px(compact ? 16 : 24), Px(compact ? 12 : 18), Px(compact ? 16 : 24), Px(compact ? 8 : 12));
            page.RowStyles[0].Height = Px(compact ? 40 : 48); page.RowStyles[2].Height = Px(compact ? 36 : 42);
            page.RowStyles[4].Height = Px(UiLanguage.English ? 126 : compact ? 86 : 96); page.RowStyles[5].Height = Px(compact ? 24 : 27);
            body.ColumnStyles[1].Width = Px(compact ? 310 : 376); left.Margin = new(0, 0, Px(compact ? 12 : 18), 0);
            left.RowStyles[1].Height = Px(compact ? 50 : 67); summary.Padding = new(Px(12), Px(4), Px(8), Px(4));
            source.Width = Px(compact ? 300 : 480); layout.Width = Px(compact ? 220 : 280);
            refresh.Text = compact ? "刷新" : "刷新设备"; refresh.Width = Px(compact ? 58 : 88);
            autoMatch.Text = automaticSource ? "自动匹配：开" : "自动匹配：关"; autoMatch.Width = Px(118);
            actions.Padding = new(0, Px(compact ? 6 : 10), 0, 0);
            foreach (Control c in actions.Controls) c.Height = Px(compact ? 32 : 35);
            systemSound.Width = Px(152);
            int[] actionWidths = { 158, 104, 96, 110, 96, 96, 88, 88, 142, 132 };
            if (UiLanguage.English) actionWidths = new[] { 146, 132, 124, 132, 132, 132, 148, 68, 195, 150 };
            for (int i = 0; i < actions.Controls.Count; i++) actions.Controls[i].Width = Px(actionWidths[i]);
            if (UiLanguage.English) { autoMatch.Width = Px(138); systemSound.Width = Px(186); refresh.Width = Px(compact ? 80 : 142); }
            page.ResumeLayout(true); body.PerformLayout(); left.PerformLayout(); LayoutInspector(compact); map.Invalidate();
        }
        finally { fittingPage = false; }
    }
    void LayoutInspector(bool compact)
    {
        if (fittingInspector) return;
        fittingInspector = true;
        inspector.SuspendLayout();
        var scroll = inspector.AutoScrollPosition;
        int[] ys = { 12, 36, 0, 76, 95, 131, 150, 186, 205, 241, 241, 260, 260, 298, 340, 382, 183, 219 };
        int usable = Math.Max(Px(230), inspector.ClientSize.Width - Px(32));
        for (int i = 0; i < inspectorSpec.Count; i++)
        {
            var item = inspectorSpec[i]; var c = item.Control;
            c.Visible = !(compact && i == 2) && !(automaticSource && (i == 3 || i == 4));
            int x = compact ? 16 : item.X;
            int y = compact ? ys[i] : item.Y;
            if (i >= 7 && i <= 15) y += 72;
            if (automaticSource && i >= 5) y -= compact ? 55 : 74;
            if (compact && (i == 10 || i == 12)) x = 16 + (int)(usable / (DeviceDpi / 96f) / 2) + 5;

            c.Location = new(Px(x) + scroll.X, Px(y) + scroll.Y);
            if (item.Width > 0) c.Width = usable;
            if (i == 13) c.Height = Px(36);
            if (i == 11 || i == 12) c.Width = compact ? usable / 2 - Px(5) : Px(136);
            if (i == 14) c.Height = Px(32);
            if (i == 15) { c.Width = usable; c.Height = Px(UiLanguage.English ? 176 : 108); }
        }
        inspector.ResumeLayout(true);
        fittingInspector = false;
    }
    void FitWindowToScreen()
    {
        var area = Screen.FromControl(this).WorkingArea;
        MinimumSize = new(Math.Min(Px(760), area.Width - 20), Math.Min(Px(560), area.Height - 20));
        if (WindowState == FormWindowState.Normal)
        {
            Size = new(Math.Min(Width, area.Width - 20), Math.Min(Height, area.Height - 20));
            Location = new(Math.Clamp(Left, area.Left, area.Right - Width), Math.Clamp(Top, area.Top, area.Bottom - Height));
        }
    }
    static ComboBox Combo(int width) => new ModernComboBox { Width = width, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = Math.Max(width, 480), IntegralHeight = false, MaxDropDownItems = 16, Margin = new(4, 3, 8, 3) };
    static Label Label(string text, float size, bool bold = false) => new() { Text = text, AutoSize = true, Font = UiFont(size, bold), ForeColor = Ink };
    static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new(0, 9, 6, 3), ForeColor = MutedInk };
    static Button Button(string text, Action action)
    {
        var b = new ModernButton { Text = text, Font = UiFont(14), Height = 35, Width = text.Length * 15 + 28, Margin = new(0, 2, 9, 2), BackColor = Color.White };
        b.FlatAppearance.BorderColor = Color.FromArgb(213, 222, 233);
        b.Click += (_, _) => { try { action(); } catch (Exception ex) { UiMessage.Show(ex.Message, "ChannelBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning); } }; return b;
    }
    static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    void RefreshDevices()
    {
        var priorSource = Source; string previous = Source?.Id ?? ""; bool hadSource = Source != null;
        try
        {
            devices = AudioEngine.Devices(); loading = true; source.Items.Clear(); source.Items.AddRange(devices.Cast<object>().ToArray());
            var selected = devices.FirstOrDefault(d => d.Id == previous) ?? (hadSource ? null : devices.FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)));
            if (selected == null && hadSource) { selected = new Endpoint(previous, "[未连接] 原音源，请重新选择", 0, 0, 0); source.Items.Add(selected); }
            source.SelectedItem = selected;
            if (automaticSource && selected != null && (!hadSource || priorSource!.Channels != selected.Channels || priorSource.Mask != selected.Mask))
                foreach (var s in speakers) s.SourceChannel = SpeakerLayouts.Match(selected, CurrentLayout, s.Role);
            status.Text = $"已发现 {devices.Count} 个播放设备。选择布局，点击音箱，在右侧配置其设备和 L/R 输出。";
        }
        catch (Exception ex) { status.Text = "设备读取失败：" + ex.Message; }
        finally { loading = false; Render(); }
    }
    void ChangeLayout()
    {
        EndTest();
        if (!preview) RefreshDevices();
        speakers = CurrentLayout.Roles.Select(r => speakers.FirstOrDefault(s => s.Role == r) ?? SpeakerLayouts.Create(CurrentLayout, Source).First(s => s.Role == r)).ToList();
        if (automaticSource) foreach (var s in speakers) s.SourceChannel = SpeakerLayouts.Match(Source, CurrentLayout, s.Role);
        if (!speakers.Any(s => s.Role == selectedRole)) selectedRole = speakers[0].Role;
        status.Text = $"已切换为 {CurrentLayout.Name}；按实际音源重新匹配声道，保留输出设备、接口、增益和延迟。"; Render();
    }
    void MatchSource()
    {
        EndTest(); foreach (var s in speakers) s.SourceChannel = SpeakerLayouts.Match(Source, CurrentLayout, s.Role);
        int missing = speakers.Count(s => s.SourceChannel < 0);
        status.Text = Source == null ? "请选择虚拟播放音源。" : missing == 0 ? "已按扬声器位置匹配全部音源声道。" : $"{missing} 只音箱没有匹配音源声道，请手动选择；不会自动补出中置或低音信号。"; Render();
    }
    void SelectSpeaker(string role) { selectedRole = role; RenderInspector(); map.SelectedRole = role; map.Invalidate(); }
    void Render()
    {
        if (automaticSource) foreach (var s in speakers) s.SourceChannel = SpeakerLayouts.Match(Source, CurrentLayout, s.Role);
        sourceChannel.Enabled = !automaticSource;
        LayoutInspector(page.Width < Px(1180) || page.Height < Px(840));
        autoMatch.Text = automaticSource ? "自动匹配：开" : "自动匹配：关";
        map.Speakers = speakers; map.SelectedRole = selectedRole; map.Devices = devices; map.Source = Source; map.LayoutName = CurrentLayout.Name; map.Invalidate();
        int ready = speakers.Count(IsReady), missing = speakers.Count(s => s.SourceChannel < 0 || s.SourceChannel >= (Source?.Channels ?? 0));
        info.Text = Source == null ? "请选择虚拟播放音源；布局可先行配置。安装虚拟驱动后点击“刷新设备”。" :
            $"实际音源 {Source.Channels} ch · {Source.Rate / 1000.0:g} kHz    /    目标布局 {speakers.Count} ch。应用配置将同步 Windows 声道模式并匹配路由。";
        var mismatched = speakers.Where(s => !s.Muted && s.DeviceId.Length > 0 && s.SourceChannel >= 0 && SpeakerLayouts.Match(Source, CurrentLayout, s.Role) >= 0 && s.SourceChannel != SpeakerLayouts.Match(Source, CurrentLayout, s.Role)).Select(s => s.Role).ToArray();
        if (mismatched.Length > 0) info.Text = "注意：" + string.Join("、", mismatched) + " 的音源声道与音箱位置不符；若非有意交叉映射，请点击“匹配声道”，再应用配置。";
        summary.Text = $"{CurrentLayout.Name}    ·    {speakers.Count} 只音箱    ·    {ready} 只已就绪    ·    {speakers.Where(s => s.DeviceId.Length > 0).Select(s => s.DeviceId).Distinct().Count()} 台输出设备\n蓝框为选中音箱；图标下显示独立增益。点击音箱或使用右侧下拉框选择。"; RenderInspector();
    }
    bool IsReady(SpeakerSetting s) => s.SourceChannel >= 0 && s.SourceChannel < (Source?.Channels ?? 0) && !s.Muted && s.DeviceId != Source?.Id && OutputReady(s) && !speakers.Any(x => x != s && x.DeviceId == s.DeviceId && x.Side == s.Side);
    bool OutputReady(SpeakerSetting s)
    {
        var device = devices.FirstOrDefault(d => d.Id == s.DeviceId);
        if (device == null) return false;
        try { SpeakerLayouts.ValidateOutput(s, device); return true; } catch { return false; }
    }
    void RenderInspector()
    {
        bool prior = loading; loading = true;
        try
        {
            speakerSelect.Items.Clear(); speakerSelect.Items.AddRange(speakers.Select(s => new SpeakerChoice(s.Role, $"{s.Role} · {SpeakerLayouts.Role(s.Role).Name}")).Cast<object>().ToArray());
            speakerSelect.SelectedItem = speakerSelect.Items.Cast<SpeakerChoice>().FirstOrDefault(s => s.Id == selectedRole);
            if (Selected is not SpeakerSetting v) return; speakerTitle.Text = $"{SpeakerLayouts.Role(v.Role).Name}  {v.Role}";
            sourceChannel.Items.Clear(); sourceChannel.Items.Add(new ChannelChoice(-1, "— 未连接音源 —"));
            sourceChannel.Items.AddRange(SpeakerLayouts.ChannelNames(Source).Select((s, i) => new ChannelChoice(i, s)).Cast<object>().ToArray());
            if (v.SourceChannel >= (Source?.Channels ?? 0)) sourceChannel.Items.Add(new ChannelChoice(v.SourceChannel, $"[不可用] 原声道 {v.SourceChannel + 1}"));
            sourceChannel.SelectedItem = sourceChannel.Items.Cast<ChannelChoice>().FirstOrDefault(c => c.Index == v.SourceChannel);
            destination.Items.Clear(); destination.Items.Add(new DeviceChoice("", "— 未分配设备 —"));
            destination.Items.AddRange(devices.Where(d => d.Channels >= 1 && d.Id != Source?.Id).Select(d => new DeviceChoice(d.Id, d.ToString())).Cast<object>().ToArray());
            if (v.DeviceId.Length > 0 && !destination.Items.Cast<DeviceChoice>().Any(d => d.Id == v.DeviceId)) destination.Items.Add(new DeviceChoice(v.DeviceId, v.DeviceId == Source?.Id ? "[无效] 不能回接音源" : "[未连接] 原输出设备"));
            destination.SelectedItem = destination.Items.Cast<DeviceChoice>().FirstOrDefault(d => d.Id == v.DeviceId);
            var device = devices.FirstOrDefault(d => d.Id == v.DeviceId);
            nativeOutput.Enabled = device != null;
            quickMap.Enabled = device != null;
            nativeOutput.Checked = v.NativeOutput;
            outputSide.Items.Clear();
            var names = v.NativeOutput && device != null ? SpeakerLayouts.ChannelNames(device) : new[] { "L · 设备左声道", "R · 设备右声道" };
            outputSide.Items.AddRange(names.Select((name, index) => new ChannelChoice(index, name)).Cast<object>().ToArray());
            if (v.Side >= names.Length) outputSide.Items.Add(new ChannelChoice(v.Side, $"[不可用] 原声道 {v.Side + 1}"));
            outputSide.SelectedItem = outputSide.Items.Cast<ChannelChoice>().FirstOrDefault(c => c.Index == v.Side);
            gain.Value = (decimal)v.GainDb; delay.Value = v.DelayMs; deviceVolume.Bind(v.DeviceId == Source?.Id ? "" : v.DeviceId, preview); UpdateDetail();
        }
        finally { loading = prior; }
    }
    void EditSelected()
    {
        if (loading || Selected is not SpeakerSetting s) return; EndTest();
        s.SourceChannel = (sourceChannel.SelectedItem as ChannelChoice)?.Index ?? -1;
        s.Side = (outputSide.SelectedItem as ChannelChoice)?.Index ?? 0;
        if (s.NativeOutput && devices.FirstOrDefault(d => d.Id == s.DeviceId) is { } device) { s.DeviceChannelMask = device.Mask; s.DeviceChannels = device.Channels; }
        s.GainDb = (float)gain.Value; s.DelayMs = (int)delay.Value; Render();
    }
    void ChangeDestination()
    {
        if (loading || Selected is not SpeakerSetting s) return;
        s.DeviceId = (destination.SelectedItem as DeviceChoice)?.Id ?? "";
        s.NativeOutput = speakers.FirstOrDefault(x => x != s && x.DeviceId == s.DeviceId)?.NativeOutput ?? false;
        if (devices.FirstOrDefault(d => d.Id == s.DeviceId) is { } device)
        {
            s.DeviceChannelMask = device.Mask; s.DeviceChannels = device.Channels;
            int matched = s.NativeOutput ? Array.IndexOf(SpeakerLayouts.SourceRoles(device), s.Role) : -1;
            s.Side = matched >= 0 ? matched : s.Side < (s.NativeOutput ? device.Channels : 2) ? s.Side : 0;
        }
        Render();
        if (!preview && devices.FirstOrDefault(d => d.Id == s.DeviceId)?.Channels > 2) QuickMapDevice();
    }
    void ChangeOutputMode()
    {
        if (loading || Selected is not SpeakerSetting selected || devices.FirstOrDefault(d => d.Id == selected.DeviceId) is not { } device) return;
        foreach (var s in speakers.Where(s => s.DeviceId == selected.DeviceId))
        {
            s.NativeOutput = nativeOutput.Checked; s.DeviceChannelMask = device.Mask; s.DeviceChannels = device.Channels;
        }
        status.Text = "输出模式对同一设备的所有音箱生效；请检查各声道后应用配置。";
        Render();
        if (!preview && selected.NativeOutput) QuickMapDevice();
    }
    void QuickMapDevice()
    {
        if (Selected is not SpeakerSetting selected || devices.FirstOrDefault(d => d.Id == selected.DeviceId) is not { } device) return;
        var targets = SpeakerLayouts.QuickMapTargets(speakers, device);
        if (targets.Count == 0) { UiMessage.Show(this, "驱动未报告可匹配的位置，请手动映射输出声道。"); return; }
        string Describe(SpeakerSetting s) => s.Role + " · " + UiLanguage.T(SpeakerLayouts.Role(s.Role).Name);
        string mapping = string.Join("\n", targets.Select(t => $"CH{t.Channel + 1} → {Describe(t.Speaker)}"));
        var replaced = targets.Where(t => t.Speaker.DeviceId.Length > 0 && t.Speaker.DeviceId != device.Id).Select(t => Describe(t.Speaker));
        var removed = speakers.Where(s => s.DeviceId == device.Id && targets.All(t => t.Speaker != s)).Select(Describe);
        string message = device.Name + "\n\n" + UiLanguage.T("是否按设备声道位置快速映射？") + "\n" + mapping;
        if (replaced.Any()) message += "\n\n" + UiLanguage.T("将替换这些音箱的原设备：") + string.Join(", ", replaced);
        if (removed.Any()) message += "\n\n" + UiLanguage.T("将解除该设备上无法匹配的位置：") + string.Join(", ", removed);
        message += "\n\n" + UiLanguage.T("其他设备上未匹配的位置将保留。确认后仍需点击“应用配置”。");
        if (UiMessage.Show(this, message, "快速映射", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        SpeakerLayouts.ApplyQuickMap(speakers, device); Render();
    }
    void UpdateDetail()
    {
        if (Selected is not SpeakerSetting s) return;
        bool conflict = s.DeviceId.Length > 0 && speakers.Any(x => x != s && x.DeviceId == s.DeviceId && x.Side == s.Side);
        detail.Text = (conflict ? "输出冲突：另一只音箱占用了相同的设备声道。" : IsReady(s) ? "连接已就绪。" : "请检查音源、输出声道和设备布局。") + "\n" + (s.NativeOutput ? "当前发送设备多声道。" : "当前仅发送立体声。") + "\n测试使用当前预设的增益与延迟，跳过静音音箱。\n测试期间暂停路由，结束后自动恢复。";
        detail.ForeColor = conflict ? Color.FromArgb(190, 76, 49) : MutedInk;
    }
    void SetRunning(bool v) { stop.Enabled = v; }
    SurroundProfile Draft() => new() { SourceId = Source?.Id ?? "", LayoutId = CurrentLayout.Id, Speakers = speakers, AutoMatchSource = automaticSource, FollowSourceVolume = followVolume, ServicePriority = servicePriority, BufferMs = buffers.QueueMs, CaptureBufferMs = buffers.CaptureMs, OutputBufferMs = buffers.OutputMs };
    async void ServiceAction(Action action)
    {
        if (serviceBusy || preview) return;
        serviceBusy = true; run.Enabled = stop.Enabled = test.Enabled = false;
        try { await Task.Run(action); if (!IsDisposed) UpdateMeters(); }
        catch (Exception ex) { if (!IsDisposed) UiMessage.Show(this, ex.Message, "后台服务", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { serviceBusy = false; if (!IsDisposed) { run.Enabled = test.Enabled = true; stop.Enabled = ServiceFiles.ReadSaved()?.Enabled == true; } }
    }
    void Commit(bool? enabled)
    {
        RefreshDevices();
        if (Source is not Endpoint input) throw new InvalidOperationException("请选择音源。");
        // Snapshot before the asynchronous installation: edits made during UAC stay as UI drafts.
        var profile = SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(Draft()), devices);
        ProfileApplication.Validate(profile);
        ServiceAction(() => ProfileApplication.Apply(new ApplyRequest(profile, enabled)));
    }
    void Start() => Commit(true);
    void MeasureLatency()
    {
        if (serviceBusy || preview) return;
        using var dialog = new CalibrationForm(SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(Draft()), devices));
        dialog.ShowDialog(this); RestoreSaved();
    }
    void ToggleVolumeFollow() { followVolume = !followVolume; volumeFollow.Text = followVolume ? "系统音量：跟随" : "系统音量：直通"; }
    void RequestTest(bool all)
    {
        bool cancel = serviceReport?.State == "Testing";
        var profile = cancel ? null : SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(Draft()), devices);
        var role = all ? null : selectedRole;
        if (!cancel) { using var validate = new AudioTestSequence(new TestRequest("validate", DateTime.UtcNow, profile, role), false); }
        ServiceAction(() => {
            ServiceSetup.EnsureStarted();
            ServiceFiles.AtomicWrite(Path.Combine(ServiceFiles.Root, "test.json"), new TestRequest(Guid.NewGuid().ToString("N"), DateTime.UtcNow.AddSeconds(30), profile, role, cancel));
        });
    }
    void ApplyConfiguration() => Commit(null);
    void Stop() => ServiceAction(() => {
        var saved = ServiceFiles.ReadSaved();
        if (saved != null) ServiceFiles.AtomicWrite(ServiceFiles.ConfigPath, saved with { Enabled = false, Revision = Guid.NewGuid().ToString("N") });
    });
    void RestartService() => ServiceAction(ServiceSetup.Restart);
    void UpdateMeters()
    {
        deviceVolume.RefreshDevice();
        if (preview || serviceBusy) return;
        if (DateTime.UtcNow >= nextSourceRefresh)
        {
            nextSourceRefresh = DateTime.UtcNow.AddSeconds(2);
            try
            {
                if (Source is { } selected)
                {
                    var actual = AudioEngine.DeviceFormat(selected.Id);
                    if (actual != (selected.Channels, selected.Mask, selected.Rate)) RefreshDevices();
                }
            }
            catch { }
        }
        var saved = ServiceFiles.ReadSaved(); SetRunning(saved?.Enabled == true);
        try
        {
            serviceReport = JsonSerializer.Deserialize<ServiceReport>(File.ReadAllText(Path.Combine(ServiceFiles.Root, "status.json")));
            bool testing = serviceReport?.State == "Testing" && DateTime.UtcNow - serviceReport.UpdatedUtc < TimeSpan.FromSeconds(5);
            test.Text = testing ? "■ 停止测试" : "▶ 播放测试音频";
            testAll.Text = testing ? "■ 停止测试" : "▶ 测试配置";
            if (serviceReport == null || DateTime.UtcNow - serviceReport.UpdatedUtc > TimeSpan.FromSeconds(5)) { status.Text = "服务未运行或正在重启；已保存的配置会在服务恢复后继续执行。"; return; }
            status.Text = saved?.Revision != serviceReport.Revision ? "配置已保存，等待后台服务应用…" : serviceReport.Message + (serviceReport.Frames > 0 ? $" · {serviceReport.Frames:N0} 帧" : "");
        }
        catch { status.Text = "服务尚未就绪。点击“启动服务 / 路由”；首次安装会请求管理员授权。"; }
    }
    void RestoreSaved()
    {
        var saved = ServiceFiles.ReadSaved(); if (saved == null) return;
        SetProfile(saved.Profile); UpdateMeters();
    }
    void SetProfile(SurroundProfile p)
    {
        automaticSource = p.AutoMatchSource; followVolume = p.FollowSourceVolume;
        volumeFollow.Text = followVolume ? "系统音量：跟随" : "系统音量：直通";
        loading = true;
        try
        {
            layout.SelectedItem = SpeakerLayouts.Get(p.LayoutId); speakers = p.Speakers; selectedRole = speakers[0].Role;
            servicePriority = p.ServicePriority; buffers = p.Buffers; lowLatency.Checked = buffers != BufferSettings.Standard; RefreshBufferLabel();
            var input = devices.FirstOrDefault(d => d.Id == p.SourceId);
            if (input == null && p.SourceId.Length > 0) { input = new Endpoint(p.SourceId, "[未连接] 原音源", 0, 0, 0); source.Items.Add(input); }
            source.SelectedItem = input;
        }
        finally { loading = false; }
        Render();
    }
    void EndTest() { }
    void SaveProfile()
    {
        using var dialog = new SaveFileDialog { Filter = UiLanguage.T("ChannelBridge 配置|*.json"), FileName = UiLanguage.T("环绕声配置.json") }; if (dialog.ShowDialog() != DialogResult.OK) return;
        var p = Draft();
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true })); status.Text = "配置已保存：" + dialog.FileName;
    }
    void LoadProfile()
    {
        using var dialog = new OpenFileDialog { Filter = UiLanguage.T("ChannelBridge 配置|*.json") }; if (dialog.ShowDialog() != DialogResult.OK) return;
        var p = SpeakerLayouts.ReadProfile(File.ReadAllText(dialog.FileName), devices); EndTest(); loading = true;
        automaticSource = p.AutoMatchSource; followVolume = p.FollowSourceVolume;
        volumeFollow.Text = followVolume ? "系统音量：跟随" : "系统音量：直通";
        try
        {
            layout.SelectedItem = SpeakerLayouts.Get(p.LayoutId); speakers = p.Speakers; selectedRole = speakers[0].Role;
            servicePriority = p.ServicePriority; buffers = p.Buffers; lowLatency.Checked = buffers != BufferSettings.Standard; RefreshBufferLabel();
            var input = devices.FirstOrDefault(d => d.Id == p.SourceId);
            if (input == null && p.SourceId.Length > 0) { input = new Endpoint(p.SourceId, "[未连接] 原音源，请重新选择", 0, 0, 0); source.Items.Add(input); } source.SelectedItem = input;
        }
        finally { loading = false; }
        Render(); status.Text = "配置已载入；缺失设备保留为未连接状态，请检查后启动。";
    }
    public void PreparePreview()
    {
        preview = true; timer.Stop();
        loading = true;
        try
        {
            layout.SelectedItem = SpeakerLayouts.Get("7.1"); var fixture = new Endpoint("preview-source", "演示虚拟音源（非设备检测结果）", 8, 48000, 0x63f);
            source.Items.Add(fixture); source.SelectedItem = fixture; devices = Enumerable.Range(1, 4).Select(n => new Endpoint("preview-" + n, $"演示 USB 声卡 {n}", 2, 48000, 3)).ToList();
            speakers = SpeakerLayouts.Create(CurrentLayout, fixture); string[] paired = { "FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR" };
            for (int i = 0; i < paired.Length; i++) { var s = speakers.First(x => x.Role == paired[i]); s.DeviceId = devices[i / 2].Id; s.Side = i % 2; s.GainDb = i == 3 ? -6 : i >= 4 ? -3 : -1.5f; } selectedRole = "FL";
        }
        finally { loading = false; }
        Render(); status.Text = "界面演示 · 使用示例设备，尚未启动音频路由。";
    }
    public string VerifyUi()
    {
        PreparePreview();
        Show(); Application.DoEvents();
        VerifyCaption();
        string beforeLanguage = JsonSerializer.Serialize(Draft()); bool priorLanguage = UiLanguage.English;
        UiLanguage.Set(true, false);
        if (run.Text != "▶ Start routing" || systemSound.Text != "System sound settings") throw new Exception("English UI translation failed.");
        UiLanguage.Set(false, false);
        if (run.Text != "▶ 启动服务 / 路由" || systemSound.Text != "系统声音设置") throw new Exception("Chinese UI restoration failed.");
        UiLanguage.Set(priorLanguage, false);
        if (JsonSerializer.Serialize(Draft()) != beforeLanguage) throw new Exception("Language switch changed draft routing.");
        lowLatency.Checked = true;
        var lowProfile = SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(Draft()), devices);
        if (Source is { } previewSource && devices.All(d => d.Id != previewSource.Id)) devices.Add(previewSource);
        lowLatency.Checked = false; SetProfile(lowProfile);
        if (!lowLatency.Checked || Draft().BufferMs != 50) throw new Exception("Low latency draft restore failed.");
        lowLatency.Checked = false;
        automaticSource = true; Render();
        if (sourceChannel.Visible || inspectorSpec[3].Control.Visible) throw new Exception("Automatic source controls must be hidden.");
        int automaticY = destination.Top;
        automaticSource = false; Render();
        if (!sourceChannel.Visible || !inspectorSpec[3].Control.Visible || destination.Top <= automaticY) throw new Exception("Manual source controls must restore layout.");
        automaticSource = true; Render();
        if (systemSound.Parent == actions || systemSound.Dock != DockStyle.Right) throw new Exception("System sound link must be at top right.");
        if (FirstRunSetup.HasCable(Array.Empty<Endpoint>()) || !FirstRunSetup.HasCable(new[] { new Endpoint("test", "CABLE Input (VB-Audio Virtual Cable)", 2, 48000, 3) })) throw new Exception("First-run CABLE detection failed.");
        foreach (var size in new[] { new Size(1440, 990), new Size(1180, 880), new Size(900, 650), new Size(760, 560) })
        {
            Size = new(Px(size.Width), Px(size.Height)); FitPage(); PerformLayout();
            foreach (var c in SpeakerLayouts.All)
            {
                layout.SelectedItem = c;
                if (speakers.Count != c.Roles.Length || map.Speakers.Count != c.Roles.Length) throw new Exception("Layout UI mismatch: " + c.Id);
                if (map.Width > left.ClientSize.Width || body.Right > page.ClientSize.Width) throw new Exception("Child layout extends beyond its viewport.");
                using var bitmap = new Bitmap(map.Width, map.Height); map.DrawToBitmap(bitmap, new Rectangle(0, 0, map.Width, map.Height)); map.VerifyGeometry();
            }
        }
        map.SelectAt(map.SpeakerCenter("SR")); if (Selected?.Role != "SR") throw new Exception("Diagram selection did not update inspector.");
        DeviceVolumeControl.Verify();
        SelectSpeaker("LFE"); Selected!.Muted = true; gain.Value = -8; delay.Value = 37;
        if (Selected?.GainDb != -8 || Selected.DelayMs != 37 || !Selected.Muted) throw new Exception("Inspector failed to update selected speaker.");
        SelectSpeaker("FL"); if (Selected?.GainDb == -8) throw new Exception("Gain leaked into a different speaker.");
        SetRunning(true); if (!gain.Enabled || !layout.Enabled || !speakerSelect.Enabled || !map.Enabled) throw new Exception("Running draft editing broken."); SetRunning(false);
        Size = new(Px(760), Px(560)); FitPage();
        if (page.Width < Px(860) || page.Height < Px(600) || !viewport.AutoScroll) throw new Exception("Small-window scroll fallback failed.");
        layout.SelectedItem = SpeakerLayouts.Get("4.0");
        var rear = speakers.Single(s => s.Role == "BL"); rear.SourceChannel = 2; rear.GainDb = 2; rear.Side = 1; rear.DelayMs = 17;
        string rearDevice = rear.DeviceId;
        layout.SelectedItem = SpeakerLayouts.Get("5.1-back");
        rear = speakers.Single(s => s.Role == "BL");
        if (rear.SourceChannel != 4 || rear.GainDb != 2 || rear.Side != 1 || rear.DelayMs != 17 || rear.DeviceId != rearDevice)
            throw new Exception("Layout change retained quad source index or changed physical output settings.");
        var nativeDevice = new Endpoint("test-native", "Demo 5.1", 6, 48000, 0x3f); devices.Add(nativeDevice);
        layout.SelectedItem = SpeakerLayouts.Get("7.1"); SelectSpeaker("FL");
        destination.SelectedItem = destination.Items.Cast<DeviceChoice>().Single(d => d.Id == nativeDevice.Id);
        nativeOutput.Checked = true;
        if (outputSide.Items.Count != 6 || !Selected!.NativeOutput) throw new Exception("Native output selector did not expose all six channels");
        SpeakerLayouts.ApplyQuickMap(speakers, nativeDevice); SelectSpeaker("FC");
        if (Selected!.Side != 2 || (outputSide.SelectedItem as ChannelChoice)?.Index != 2) throw new Exception("Center output selector mismatch");
        nativeOutput.Checked = false;
        if (speakers.Where(s => s.DeviceId == nativeDevice.Id).Any(s => s.NativeOutput) || IsReady(Selected!)) throw new Exception("Stereo mode must invalidate higher channels on the whole device");
        nativeOutput.Checked = true;
        var roundTrip = SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(Draft()), devices);
        SetProfile(roundTrip);
        if (Draft().Speakers.Count(s => s.NativeOutput) != 6) throw new Exception("Native mappings lost on profile restore");
        ShowAdvancedMenu();
        var priorityMenu = (ToolStripMenuItem)advancedMenu.Items[2];
        if (priorityMenu.DropDownItems.Count != 5) throw new Exception("Missing service priority choices");
        ((ToolStripMenuItem)priorityMenu.DropDownItems[3]).PerformClick(); advancedMenu.Close();
        if (Draft().ServicePriority != "AboveNormal") throw new Exception("Priority menu did not update draft");
        var savedPriority = SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(Draft()), devices);
        servicePriority = "Normal"; SetProfile(savedPriority); ShowAdvancedMenu();
        if (!((ToolStripMenuItem)((ToolStripMenuItem)advancedMenu.Items[2]).DropDownItems[3]).Checked) throw new Exception("Priority menu did not restore saved selection");
        advancedMenu.Close();
        return $"PASS: all 13 layouts populate diagram and inspector at {DeviceDpi} DPI\nPASS: no speaker overlap/clipping at default and compact sizes\nPASS: clicking diagram selects inspector speaker\nPASS: independent speaker gain, delay and mute editing\nPASS: running allows draft edits and speaker inspection\nPASS: small windows preserve the logical layout with scrolling\n";
    }
    public void PreviewLayout(string id) { layout.SelectedItem = SpeakerLayouts.Get(id); }
    public void PrepareMultichannelPreview()
    {
        var device = new Endpoint("preview-native", "演示 5.1 音频设备", 6, 48000, 0x3f);
        devices.Add(device); layout.SelectedItem = SpeakerLayouts.Get("7.1");
        SpeakerLayouts.ApplyQuickMap(speakers, device); SelectSpeaker("FC"); Render();
    }
    record SpeakerChoice(string Id, string Name) { public override string ToString() => Name; }
    record DeviceChoice(string Id, string Name) { public override string ToString() => Name; }
    record ChannelChoice(int Index, string Name) { public override string ToString() => Name; }
}
