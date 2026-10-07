using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Runtime.InteropServices;

namespace ChannelBridge;

public record Endpoint(string Id, string Name, int Channels, int Rate, int Mask)
{
    public override string ToString() => $"{Name}  [{Channels} ch · {Rate / 1000.0:g} kHz]";
}
public record Route(string DeviceId, int Left, int Right, float Gain, int DelayMs, float? RightGain = null, int? RightDelayMs = null);
public record Profile(string SourceId, int Mode, List<Route> Routes);

// Each physical device owns an independent clock. A small continuously adjusted
// read ratio keeps its FIFO near the target, instead of periodically dropping blocks.
public sealed class AdaptiveStereo : ISampleProvider
{
    readonly object gate = new();
    readonly float[] ring;
    readonly int capacity, target, sourceRate, left, right;
    readonly float[] gains;
    readonly int[] offsets;
    readonly int maxOffset;
    long written;
    double read, ratio = 1;
    bool primed;
    public volatile float MasterGain = 1;
    public WaveFormat WaveFormat { get; }
    public long Underruns { get; private set; }
    public long Overruns { get; private set; }
    public float Peak { get; private set; }
    public double BufferedMs { get { lock (gate) return Math.Max(0, written - read) * 1000 / sourceRate; } }
    public double CorrectionPpm { get { lock (gate) return (ratio - 1) * 1e6; } }

    public AdaptiveStereo(int inputRate, int outputRate, int l, int r, float volume, int delayMs, float? rightVolume = null, int? rightDelayMs = null)
    {
        sourceRate = inputRate; left = l; right = r;
        gains = new[] { volume, rightVolume ?? volume };
        int maxDelay = Math.Max(delayMs, rightDelayMs ?? delayMs);
        offsets = new[] { inputRate * (maxDelay - delayMs) / 1000, inputRate * (maxDelay - (rightDelayMs ?? delayMs)) / 1000 };
        maxOffset = offsets.Max();
        capacity = inputRate * 3; ring = new float[capacity * 2];
        target = inputRate * (80 + maxDelay) / 1000;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outputRate, 2);
    }
    public void Push(float[] samples, int frames, int channels)
    {
        lock (gate)
        {
            for (int f = 0; f < frames; f++)
            {
                int p = (int)(written % capacity) * 2;
                ring[p] = left < 0 ? 0 : samples[f * channels + left] * gains[0];
                ring[p + 1] = right < 0 ? 0 : samples[f * channels + right] * gains[1];
                written++;
            }
            if (written - read >= capacity - 2)
            {
                read = written - target; primed = false; Overruns++;
            }
        }
    }
    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        lock (gate)
        {
            Peak = 0;
            if (!primed)
            {
                if (written - read < target + 2) return count;
                // Preserve the target delay even after restarting an idle source.
                read = written - target; primed = true;
            }
            var error = (written - read - target) / sourceRate;
            double desired = 1 + Math.Clamp(error * 0.1, -0.003, 0.003);
            ratio += (desired - ratio) * 0.02;
            double step = (double)sourceRate / WaveFormat.SampleRate * ratio;
            for (int i = 0; i + 1 < count; i += 2)
            {
                if (read + maxOffset + 1 >= written)
                {
                    Underruns++; primed = false; read = written; break;
                }
                for (int c = 0; c < 2; c++)
                {
                    double position = read + offsets[c];
                    long a = (long)position;
                    int p = (int)(a % capacity) * 2, q = (int)((a + 1) % capacity) * 2;
                    float frac = (float)(position - a);
                    float value = ring[p + c] + (ring[q + c] - ring[p + c]) * frac;
                    value *= MasterGain;
                    value = Math.Clamp(float.IsFinite(value) ? value : 0, -1, 1);
                    buffer[offset + i + c] = value;
                    Peak = Math.Max(Peak, Math.Abs(value));
                }
                read += step;
            }
        }
        return count;
    }
}

public sealed class AudioEngine : IDisposable
{
    readonly List<(MMDevice Device, WasapiOut Player, AdaptiveStereo Buffer)> sinks = new();
    MMDevice? source;
    WasapiLoopbackCapture? capture;
    volatile bool stopping;
    public event Action<string>? Fault;
    public IReadOnlyList<AdaptiveStereo> Buffers => sinks.Select(s => s.Buffer).ToArray();
    public int SourceChannels { get; private set; }
    public long FramesCaptured;
    bool followSourceVolume;
    public float SourceGain { get; private set; } = 1;
    public void UpdateSourceVolume()
    {
        if (source == null) return;
        float gain = followSourceVolume ? (source.AudioEndpointVolume.Mute ? 0 : source.AudioEndpointVolume.MasterVolumeLevelScalar) : 1;
        SourceGain = gain;
        foreach (var sink in sinks) sink.Buffer.MasterGain = gain;
    }

    public static List<Endpoint> Devices()
    {
        using var e = new MMDeviceEnumerator();
        var result = new List<Endpoint>();
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (d)
            {
                try
                {
                    var f = d.AudioClient.MixFormat;
                    result.Add(new(d.ID, d.FriendlyName, f.Channels, f.SampleRate,
                        ChannelMask(f)));
                }
                catch { /* Disconnected/unavailable endpoints are not selectable. */ }
            }
        }
        return result;
    }
    static int ChannelMask(WaveFormat f)
    {
        if (f is not WaveFormatExtensible) return 0;
        var pointer = WaveFormat.MarshalToPtr(f);
        try { return Marshal.ReadInt32(pointer, 20); }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    public void Start(string sourceId, IReadOnlyList<Route> routes, bool followVolume = false)
    {
        if (routes.Count == 0) throw new InvalidOperationException("至少选择一个输出设备。");
        if (routes.Any(r => r.DeviceId == sourceId)) throw new InvalidOperationException("音源不能同时作为目标设备，否则会产生反馈。");
        if (routes.Select(r => r.DeviceId).Distinct().Count() != routes.Count) throw new InvalidOperationException("每个输出设备只能使用一行，请在同一行设置左右声道。");
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            source = enumerator.GetDevice(sourceId);
            followSourceVolume = followVolume;
            capture = new WasapiLoopbackCapture(source);
            var format = capture.WaveFormat;
            SourceChannels = format.Channels;
            ValidateFormat(format);
            foreach (var r in routes)
            {
                if (r.Left < -1 || r.Right < -1 || r.Left >= format.Channels || r.Right >= format.Channels)
                    throw new InvalidOperationException("映射超出音源实际声道数，请刷新设备并重新选择。");
                if (!float.IsFinite(r.Gain) || r.Gain < 0 || r.Gain > 2 || r.DelayMs < 0 || r.DelayMs > 500 ||
                    !float.IsFinite(r.RightGain ?? r.Gain) || (r.RightGain ?? r.Gain) < 0 || (r.RightGain ?? r.Gain) > 2 ||
                    (r.RightDelayMs ?? r.DelayMs) < 0 || (r.RightDelayMs ?? r.DelayMs) > 500)
                    throw new InvalidOperationException("音量或延迟超出范围。");
                var device = enumerator.GetDevice(r.DeviceId);
                WasapiOut? player = null;
                try
                {
                    if (device.AudioClient.MixFormat.Channels < 2) throw new InvalidOperationException($"{device.FriendlyName} 不是两声道设备。");
                    var fifo = new AdaptiveStereo(format.SampleRate, device.AudioClient.MixFormat.SampleRate, r.Left, r.Right, r.Gain, r.DelayMs, r.RightGain, r.RightDelayMs);
                    player = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
                    player.Init(new SampleToWaveProvider(fifo));
                    player.PlaybackStopped += (_, a) => { if (!stopping) Fault?.Invoke(a.Exception?.Message ?? "输出设备停止播放。"); };
                    sinks.Add((device, player, fifo));
                }
                catch { player?.Dispose(); device.Dispose(); throw; }
            }
            capture.DataAvailable += (_, e) =>
            {
                if (stopping) return;
                try
                {
                    float[] data = Decode(e.Buffer, e.BytesRecorded, format);
                    int frames = data.Length / format.Channels;
                    foreach (var s in sinks) s.Buffer.Push(data, frames, format.Channels);
                    Interlocked.Add(ref FramesCaptured, frames);
                }
                catch (Exception ex) { Fault?.Invoke(ex.Message); }
            };
            capture.RecordingStopped += (_, e) => { if (!stopping) Fault?.Invoke(e.Exception?.Message ?? "音源停止捕获。"); };
            UpdateSourceVolume();
            foreach (var sink in sinks) sink.Player.Play();
            capture.StartRecording();
        }
        catch { Dispose(); throw; }
    }
    static bool IsFloat(WaveFormat f) => f.Encoding == WaveFormatEncoding.IeeeFloat ||
        f is WaveFormatExtensible ex && ex.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
    static void ValidateFormat(WaveFormat f)
    {
        bool pcm = f.Encoding == WaveFormatEncoding.Pcm || f is WaveFormatExtensible ex &&
            ex.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71");
        if (!(IsFloat(f) && f.BitsPerSample == 32) && !(pcm && (f.BitsPerSample is 16 or 24 or 32)))
            throw new NotSupportedException($"不支持音源格式：{f}。请在 Windows 中设置 16/24/32 位 PCM 或 32 位浮点。");
    }
    public static float[] Decode(byte[] bytes, int length, WaveFormat format)
    {
        ValidateFormat(format);
        int size = format.BitsPerSample / 8;
        int count = length / format.BlockAlign * format.Channels;
        var result = new float[count];
        bool floating = IsFloat(format);
        for (int i = 0, p = 0; i < count; i++, p += size)
            result[i] = floating ? BitConverter.ToSingle(bytes, p) : size switch
            {
                2 => BitConverter.ToInt16(bytes, p) / 32768f,
                3 => ((bytes[p] | bytes[p + 1] << 8 | bytes[p + 2] << 16) << 8 >> 8) / 8388608f,
                4 => BitConverter.ToInt32(bytes, p) / 2147483648f,
                _ => 0
            };
        return result;
    }
    public void Dispose()
    {
        stopping = true;
        // Capture.Dispose joins its worker before the sink list is disposed.
        if (capture != null) { try { capture.StopRecording(); } catch { } capture.Dispose(); capture = null; }
        foreach (var s in sinks) { try { s.Player.Stop(); } catch { } s.Player.Dispose(); s.Device.Dispose(); }
        sinks.Clear(); source?.Dispose(); source = null;
    }
}

public sealed class TestTone(int rate, int side, double frequency = 440) : ISampleProvider
{
    int position;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
    public int Read(float[] b, int offset, int count)
    {
        int frames = Math.Min(count / 2, Math.Max(0, (int)(rate * .8) - position));
        Array.Clear(b, offset, count);
        for (int n = 0; n < frames; n++, position++)
        {
            double t = (double)position / rate;
            double envelope = Math.Min(1, t / .02) * Math.Min(1, (.8 - t) / .04);
            b[offset + n * 2 + side] = (float)(.08 * envelope * Math.Sin(2 * Math.PI * frequency * t));
        }
        return frames * 2;
    }
}
