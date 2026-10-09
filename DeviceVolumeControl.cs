using NAudio.CoreAudioApi;

namespace ChannelBridge;

internal readonly record struct DeviceVolume(float Level, bool Muted);
internal interface IDeviceVolumeBackend
{
    DeviceVolume Read(string id);
    void SetLevel(string id, float value);
    void SetMute(string id, bool muted);
}
internal sealed class WindowsDeviceVolume : IDeviceVolumeBackend
{
    public DeviceVolume Read(string id)
    {
        using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDevice(id);
        if (device.State != DeviceState.Active) throw new IOException("设备未连接");
        return new(device.AudioEndpointVolume.MasterVolumeLevelScalar, device.AudioEndpointVolume.Mute);
    }
    public void SetLevel(string id, float value)
    {
        using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDevice(id);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0, 1);
    }
    public void SetMute(string id, bool muted)
    {
        using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDevice(id);
        device.AudioEndpointVolume.Mute = muted;
    }
}

internal sealed class DeviceVolumeControl : UserControl
{
    readonly IDeviceVolumeBackend backend;
    readonly Button toggle = new ModernButton { Dock = DockStyle.Fill, Text = "\uE767", Margin = Padding.Empty,
        Font = new Font("Segoe MDL2 Assets", 15), TabIndex = 0 };
    readonly TrackBar slider = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, TickStyle = TickStyle.None,
        AutoSize = false, SmallChange = 1, LargeChange = 5, Margin = Padding.Empty, TabIndex = 1 };
    readonly Label number = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Text = "—", Margin = Padding.Empty };
    readonly ToolTip tip = new();
    string deviceId = "";
    bool refreshing, preview;
    DeviceVolume current;
    public DeviceVolumeControl() : this(new WindowsDeviceVolume()) { }
    internal DeviceVolumeControl(IDeviceVolumeBackend backend)
    {
        this.backend = backend; BackColor = Color.White; Height = 36;
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        row.ColumnStyles.Add(new(SizeType.Percent, 14)); row.ColumnStyles.Add(new(SizeType.Percent, 68)); row.ColumnStyles.Add(new(SizeType.Percent, 18));
        row.Controls.Add(toggle, 0, 0); row.Controls.Add(slider, 1, 0); row.Controls.Add(number, 2, 0); Controls.Add(row);
        slider.ValueChanged += (_, _) => {
            if (refreshing || preview || deviceId.Length == 0) return;
            try { backend.SetLevel(deviceId, slider.Value / 100f); if (current.Muted && slider.Value > 0) backend.SetMute(deviceId, false); RefreshDevice(); }
            catch (Exception ex) { Unavailable(ex.Message); }
        };
        toggle.Click += (_, _) => {
            if (preview || deviceId.Length == 0) return;
            try { backend.SetMute(deviceId, !backend.Read(deviceId).Muted); RefreshDevice(); }
            catch (Exception ex) { Unavailable(ex.Message); }
        };
    }
    public void Bind(string id, bool isPreview)
    {
        deviceId = id; preview = isPreview; RefreshDevice();
    }
    public void RefreshDevice()
    {
        if (slider.Capture) return;
        string help = UiLanguage.T("设备系统音量：立即生效，同一设备的所有声道共用。拖动滑块可解除设备静音。");
        slider.AccessibleName = UiLanguage.T("设备系统音量"); AccessibleName = slider.AccessibleName;
        tip.SetToolTip(this, help); tip.SetToolTip(slider, help); tip.SetToolTip(number, help);
        if (preview || deviceId.Length == 0) { Unavailable(UiLanguage.T("未分配设备或设备不可用")); return; }
        try
        {
            current = backend.Read(deviceId); refreshing = true;
            try { slider.Value = Math.Clamp((int)Math.Round(current.Level * 100), 0, 100); }
            finally { refreshing = false; }
            slider.Enabled = toggle.Enabled = true; number.Text = slider.Value + "%";
            toggle.Text = current.Muted ? "\uE74F" : "\uE767";
            toggle.AccessibleName = UiLanguage.T(current.Muted ? "解除设备静音" : "静音设备");
            tip.SetToolTip(toggle, toggle.AccessibleName + " · " + help);
        }
        catch (Exception ex) { Unavailable(ex.Message); }
    }
    void Unavailable(string message)
    {
        slider.Enabled = toggle.Enabled = false; number.Text = "—";
        toggle.Text = "\uE74F"; tip.SetToolTip(toggle, message); tip.SetToolTip(slider, message);
    }
    protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }

    internal static void Verify()
    {
        var backend = new FakeVolume(); using var control = new DeviceVolumeControl(backend);
        control.Bind("a", false);
        if (backend.Writes != 0 || control.slider.Value != 35) throw new Exception("Binding must read, never change system volume.");
        control.slider.Value = 60;
        if (backend.Values["a"].Level != .6f || backend.Values["a"].Muted) throw new Exception("Slider must set endpoint volume and unmute.");
        control.Bind("b", false); control.slider.Value = 25;
        if (backend.Values["a"].Level != .6f || backend.Values["b"].Level != .25f) throw new Exception("Volume must target only selected device.");
        backend.Values["b"] = new(.72f, true); int before = backend.Writes; control.RefreshDevice();
        if (control.slider.Value != 72 || backend.Writes != before) throw new Exception("External changes must refresh without feedback writes.");
        control.Bind("missing", false); if (control.slider.Enabled) throw new Exception("Missing device must disable controls.");
        control.Bind("a", true); if (control.slider.Enabled || backend.Writes != before) throw new Exception("UI preview must never control real devices.");
    }
    sealed class FakeVolume : IDeviceVolumeBackend
    {
        public Dictionary<string, DeviceVolume> Values = new() { ["a"] = new(.35f, true), ["b"] = new(.9f, false) };
        public int Writes;
        public DeviceVolume Read(string id) => Values[id];
        public void SetLevel(string id, float value) { Writes++; Values[id] = Values[id] with { Level = value }; }
        public void SetMute(string id, bool muted) { Writes++; Values[id] = Values[id] with { Muted = muted }; }
    }
}
