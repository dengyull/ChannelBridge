using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Runtime.InteropServices;

namespace ChannelBridge;

public record Endpoint(string Id, string Name, int Channels, int Rate, int Mask)
{
    public override string ToString() => $"{Name}  [{Channels} ch · {Rate / 1000.0:g} kHz]";
}
public record Route(string DeviceId, int Left, int Right, float Gain, int DelayMs, float? RightGain = null, int? RightDelayMs = null, OutputChannel[]? Outputs = null, bool NativeOutput = false, int OutputMask = 0);
public record Profile(string SourceId, int Mode, List<Route> Routes);

// Low-latency queues need capture packets sooner than the standard 50 ms poll.
// Keep sleep-based capture compatible with Windows 10 versions before 1703.
sealed class ShortBufferLoopbackCapture(MMDevice device, int bufferMs) : WasapiCapture(device, false, bufferMs)
{
    protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
        base.GetAudioClientStreamFlags() | AudioClientStreamFlags.Loopback;
}

// Each physical device owns an independent clock. A small continuously adjusted
// read ratio keeps its FIFO near the target, instead of periodically dropping blocks.
public class AdaptiveOutput : ISampleProvider
{
    readonly object gate = new();
    readonly float[] ring;
    readonly int capacity, target, sourceRate, outputChannels;
    readonly int[] sources;
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

    public AdaptiveOutput(int inputRate, WaveFormat outputFormat, OutputChannel[] channels, int bufferMs = 80)
    {
        if (bufferMs is < 10 or > 1000) throw new ArgumentOutOfRangeException(nameof(bufferMs));
        if (channels.Length != outputFormat.Channels || channels.Length is < 1 or > 32) throw new ArgumentException("Invalid output channel count");
        if (channels.Any(c => c.Source < -1 || !float.IsFinite(c.Gain) || c.Gain < 0 || c.Gain > 2 || c.DelayMs is < 0 or > 500)) throw new ArgumentException("Invalid output channel settings");
        sourceRate = inputRate; outputChannels = channels.Length;
        sources = channels.Select(c => c.Source).ToArray(); gains = channels.Select(c => c.Gain).ToArray();
        int maxDelay = channels.Max(c => c.DelayMs);
        offsets = channels.Select(c => inputRate * (maxDelay - c.DelayMs) / 1000).ToArray();
        maxOffset = offsets.Max();
        capacity = inputRate * 3; ring = new float[capacity * outputChannels];
        target = inputRate * (bufferMs + maxDelay) / 1000;
        WaveFormat = outputFormat;
    }
    public void Push(float[] samples, int frames, int channels)
    {
        lock (gate)
        {
            for (int f = 0; f < frames; f++)
            {
                int p = (int)(written % capacity) * outputChannels;
                for (int c = 0; c < outputChannels; c++) ring[p + c] = sources[c] < 0 ? 0 : samples[f * channels + sources[c]] * gains[c];
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
            for (int i = 0; i + outputChannels <= count; i += outputChannels)
            {
                if (read + maxOffset + 1 >= written)
                {
                    Underruns++; primed = false; read = written; break;
                }
                for (int c = 0; c < outputChannels; c++)
                {
                    double position = read + offsets[c];
                    long a = (long)position;
                    int p = (int)(a % capacity) * outputChannels, q = (int)((a + 1) % capacity) * outputChannels;
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
    readonly List<(MMDevice Device, WasapiOut Player, AdaptiveOutput Buffer)> sinks = new();
    readonly Dictionary<string, (int Channels, int Mask, int Rate)> outputFormats = new();
    public bool OutputFormatsMatch() => outputFormats.All(p => DeviceFormat(p.Key) == p.Value);
    MMDevice? source;
    WasapiCapture? capture;
    volatile bool stopping;
    public event Action<string>? Fault;
    public IReadOnlyList<AdaptiveOutput> Buffers => sinks.Select(s => s.Buffer).ToArray();
    public int SourceChannels { get; private set; }
    public long FramesCaptured;
    public long BufferUnderruns => sinks.Sum(s => s.Buffer.Underruns);
    public long BufferOverruns => sinks.Sum(s => s.Buffer.Overruns);
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
                    using var client = d.AudioClient;
                    var f = client.MixFormat;
                    result.Add(new(d.ID, d.FriendlyName, f.Channels, f.SampleRate,
                        ChannelMask(f)));
                }
                catch { /* Disconnected/unavailable endpoints are not selectable. */ }
            }
        }
        return result;
    }
    // Runtime format checks need only the selected endpoint. Enumerating all
    // endpoints also loads unrelated drivers and their property stores.
    public static (int Channels, int Mask, int Rate) DeviceFormat(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(id);
        if (device.State != DeviceState.Active) throw new IOException("音源设备不可用。");
        using var client = device.AudioClient;
        var format = client.MixFormat;
        return (format.Channels, ChannelMask(format), format.SampleRate);
    }
    static int ChannelMask(WaveFormat f)
    {
        if (f is not WaveFormatExtensible) return 0;
        var pointer = WaveFormat.MarshalToPtr(f);
        try { return Marshal.ReadInt32(pointer, 20); }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    public void Start(string sourceId, IReadOnlyList<Route> routes, bool followVolume = false, int bufferMs = 80, int? captureBufferMs = null, int? outputBufferMs = null)
    {
        if (bufferMs is < 10 or > 1000) throw new ArgumentOutOfRangeException(nameof(bufferMs));
        if (routes.Count == 0) throw new InvalidOperationException("至少选择一个输出设备。");
        if (routes.Any(r => r.DeviceId == sourceId)) throw new InvalidOperationException("音源不能同时作为目标设备，否则会产生反馈。");
        if (routes.Select(r => r.DeviceId).Distinct().Count() != routes.Count) throw new InvalidOperationException("每个输出设备只能使用一行，请在同一行设置左右声道。");
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            source = enumerator.GetDevice(sourceId);
            followSourceVolume = followVolume;
            var buffers = new BufferSettings(captureBufferMs ?? (bufferMs == 80 ? 100 : BufferSettings.LowLatency.CaptureMs), bufferMs, outputBufferMs ?? (bufferMs == 80 ? 40 : 20));
            buffers.Validate();
            capture = new ShortBufferLoopbackCapture(source, buffers.CaptureMs);
            var format = capture.WaveFormat;
            SourceChannels = format.Channels;
            ValidateFormat(format);
            foreach (var r in routes)
            {
                var channels = r.Outputs ?? new[] { new OutputChannel(r.Left, r.Gain, r.DelayMs), new OutputChannel(r.Right, r.RightGain ?? r.Gain, r.RightDelayMs ?? r.DelayMs) };
                if (channels.Any(c => c.Source < -1 || c.Source >= format.Channels)) throw new InvalidOperationException("映射超出音源实际声道数，请刷新设备并重新选择。");
                var device = enumerator.GetDevice(r.DeviceId);
                WasapiOut? player = null;
                try
                {
                    using var formatClient = device.AudioClient;
                    var outputFormat = formatClient.MixFormat;
                    outputFormats[r.DeviceId] = (outputFormat.Channels, ChannelMask(outputFormat), outputFormat.SampleRate);
                    if (r.NativeOutput && (outputFormat.Channels != channels.Length || ChannelMask(outputFormat) != r.OutputMask))
                        throw new InvalidOperationException("设备声道布局已变化，请刷新设备并重新映射。");
                    if (!r.NativeOutput && channels.Length != 2) throw new InvalidOperationException("立体声模式只能使用 L/R 输出。");
                    var fifo = new AdaptiveOutput(format.SampleRate, OutputFormats.Float(outputFormat.SampleRate, channels.Length, r.OutputMask, r.NativeOutput), channels, bufferMs);
                    player = new WasapiOut(device, AudioClientShareMode.Shared, true, buffers.OutputMs);
                    player.Init(new FloatWaveProvider(fifo));
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
        sinks.Clear(); outputFormats.Clear(); source?.Dispose(); source = null;
    }
}

public sealed class TestTone(int rate, int side, double frequency = 440, int channels = 2, int mask = 0, bool native = false) : ISampleProvider
{
    int position;
    public WaveFormat WaveFormat { get; } = OutputFormats.Float(rate, channels, mask, native);
    public int Read(float[] b, int offset, int count)
    {
        int frames = Math.Min(count / channels, Math.Max(0, (int)(rate * .8) - position));
        Array.Clear(b, offset, count);
        for (int n = 0; n < frames; n++, position++)
        {
            double t = (double)position / rate;
            double envelope = Math.Min(1, t / .02) * Math.Min(1, (.8 - t) / .04);
            b[offset + n * channels + side] = (float)(.08 * envelope * Math.Sin(2 * Math.PI * frequency * t));
        }
        return frames * channels;
    }
}
