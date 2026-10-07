namespace ChannelBridge;

public sealed record BufferSettings(int CaptureMs, int QueueMs, int OutputMs)
{
    public static BufferSettings Standard => new(100, 80, 40);
    public static BufferSettings LowLatency => new(25, 50, 20);
    public void Validate()
    {
        if (CaptureMs is < 10 or > 500 || QueueMs is < 10 or > 1000 || OutputMs is < 10 or > 500)
            throw new ArgumentOutOfRangeException(nameof(BufferSettings), UiLanguage.T("捕获和输出缓冲范围为 10–500ms，转发缓冲范围为 10–1000ms。"));
    }
}

public sealed class BufferSettingsForm : Form
{
    readonly NumericUpDown capture = Number(500), queue = Number(1000), output = Number(500);
    public BufferSettings Settings => new((int)capture.Value, (int)queue.Value, (int)output.Value);
    static NumericUpDown Number(int max) => new() { Minimum = 10, Maximum = max, Increment = 1, Width = 160, Dock = DockStyle.Fill };
    public BufferSettingsForm(BufferSettings current)
    {
        SuspendLayout(); AutoScaleMode = AutoScaleMode.None;
        Text = UiLanguage.T("高级设置 · 音频缓冲"); Font = MainForm.UiFont(14);
        ClientSize = new(620, 330); FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent; MaximizeBox = MinimizeBox = false;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(22), ColumnCount = 2, RowCount = 5 };
        grid.ColumnStyles.Add(new(SizeType.Percent, 65)); grid.ColumnStyles.Add(new(SizeType.Percent, 35));
        for (int i = 0; i < 3; i++) grid.RowStyles.Add(new(SizeType.Absolute, 46));
        grid.RowStyles.Add(new(SizeType.Percent, 100)); grid.RowStyles.Add(new(SizeType.Absolute, 42));
        int row = 0;
        foreach (var item in new[] { ("捕获缓冲 / ms", capture), ("转发输入缓冲 / ms", queue), ("输出设备缓冲 / ms", output) })
        {
            grid.Controls.Add(new Label { Text = UiLanguage.T(item.Item1), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            grid.Controls.Add(item.Item2, 1, row++);
        }
        var note = new Label { Text = UiLanguage.T("数值越小越容易爆音。设备可能调整实际缓冲。\n确定后请点击主界面的“应用配置”；总延迟还包含音箱补偿。"), Dock = DockStyle.Fill };
        grid.Controls.Add(note, 0, 3); grid.SetColumnSpan(note, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = new ModernButton { Text = UiLanguage.T("确定"), Width = 90, Height = 32 };
        var cancel = new ModernButton { Text = UiLanguage.T("取消"), Width = 90, Height = 32, DialogResult = DialogResult.Cancel };
        var reset = new ModernButton { Text = UiLanguage.T("恢复 50ms 默认值"), Width = 220, Height = 32 };
        void Fill(BufferSettings value) { capture.Value = value.CaptureMs; queue.Value = value.QueueMs; output.Value = value.OutputMs; }
        reset.Click += (_, _) => Fill(BufferSettings.LowLatency);
        ok.Click += (_, _) => { Settings.Validate(); DialogResult = DialogResult.OK; };
        buttons.Controls.AddRange(new Control[] { ok, cancel, reset }); grid.Controls.Add(buttons, 0, 4); grid.SetColumnSpan(buttons, 2);
        Controls.Add(grid); AcceptButton = ok; CancelButton = cancel; Fill(current);
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new(96, 96); ResumeLayout(true); PerformAutoScale();
    }
}
