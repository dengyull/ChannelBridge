using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ChannelBridge;

public sealed record TestRequest(string Id, DateTime ExpiresUtc, SurroundProfile? Profile, string? Role, bool Cancel = false);

public sealed class AudioTestSequence : IDisposable
{
    readonly List<SpeakerSetting> speakers;
    readonly bool simulate;
    WasapiOut? player;
    MMDevice? device;
    MMDevice? volumeSource;
    TestSignal? signal;
    string? playbackError;
    int index = -1;
    DateTime next;
    public string Role => index >= 0 && index < speakers.Count ? speakers[index].Role : "";
    public int Index => index + 1;
    public int Count => speakers.Count;
    public AudioTestSequence(TestRequest request, bool simulate)
    {
        this.simulate = simulate;
        var p = SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(request.Profile), Array.Empty<Endpoint>());
        if (request.Role != null && !p.Speakers.Any(s => s.Role == request.Role)) throw new InvalidOperationException("测试音箱不在当前布局中。");
        // Use layout order, never JSON/property order, and never silently unmute a speaker.
        speakers = SpeakerLayouts.Get(p.LayoutId).Roles.Select(r => p.Speakers.Single(s => s.Role == r))
            .Where(s => request.Role == null || s.Role == request.Role)
            .Where(s => !s.Muted && s.DeviceId.Length > 0 && s.SourceChannel >= 0).ToList();
        if (speakers.Count == 0) throw new InvalidOperationException("没有可测试音箱：请分配音源声道和输出设备，并取消静音。");
        if (speakers.Any(s => s.DeviceId == p.SourceId)) throw new InvalidOperationException("不能向音源回接测试音。");
        if (speakers.GroupBy(s => (s.DeviceId, s.Side)).Any(g => g.Count() > 1)) throw new InvalidOperationException("测试配置包含重复的设备 L/R 输出。");
        if (!simulate)
        {
            var devices = AudioEngine.Devices();
            if (speakers.Any(s => !devices.Any(d => d.Id == s.DeviceId && d.Channels >= 2))) throw new IOException("测试设备未连接。");
            if (p.FollowSourceVolume) { using var e = new MMDeviceEnumerator(); volumeSource = e.GetDevice(p.SourceId); }
        }
    }
    public bool Tick(DateTime now)
    {
        if (Interlocked.Exchange(ref playbackError, null) is string error) throw new IOException(error);
        if (signal != null) signal.MasterGain = VolumeGain();
        if (now < next) return true;
        CloseOutput();
        if (++index >= speakers.Count) return false;
        var s = speakers[index];
        if (!simulate)
        {
            using var enumerator = new MMDeviceEnumerator(); device = enumerator.GetDevice(s.DeviceId);
            player = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
            player.PlaybackStopped += (_, e) => { if (e.Exception != null) Interlocked.Exchange(ref playbackError, e.Exception.Message); };
            using var formatClient = device.AudioClient;
            signal = new TestSignal(formatClient.MixFormat.SampleRate, s) { MasterGain = VolumeGain() };
            player.Init(signal); player.Play();
        }
        next = now.AddMilliseconds(1300 + s.DelayMs);
        return true;
    }
    void CloseOutput() { var old = player; player = null; try { old?.Stop(); } finally { old?.Dispose(); device?.Dispose(); device = null; } }
    float VolumeGain() => volumeSource == null ? 1 : volumeSource.AudioEndpointVolume.Mute ? 0 : volumeSource.AudioEndpointVolume.MasterVolumeLevelScalar;
    public void Dispose() { CloseOutput(); volumeSource?.Dispose(); volumeSource = null; }
}

// Fixed low-level tone follows configured gain and delay; the opposite output stays silent.
public sealed class TestSignal : IWaveProvider
{
    readonly TestTone tone;
    readonly float gain;
    int silenceFrames;
    public volatile float MasterGain = 1;
    public WaveFormat WaveFormat { get; }
    public TestSignal(int rate, SpeakerSetting speaker)
    {
        tone = new TestTone(rate, speaker.Side, speaker.Role == "LFE" ? 60 : 440);
        WaveFormat = tone.WaveFormat; gain = speaker.LinearGain; silenceFrames = rate * speaker.DelayMs / 1000;
    }
    public int Read(byte[] buffer, int offset, int count)
    {
        int frames = count / 8, silence = Math.Min(frames, silenceFrames); silenceFrames -= silence;
        var samples = new float[frames * 2];
        int read = tone.Read(samples, silence * 2, (frames - silence) * 2);
        int total = silence * 2 + read;
        for (int i = 0; i < total; i++) samples[i] *= gain * MasterGain;
        Buffer.BlockCopy(samples, 0, buffer, offset, total * 4);
        return total * 4;
    }
}
