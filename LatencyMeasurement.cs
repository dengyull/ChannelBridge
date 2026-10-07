using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ChannelBridge;

public sealed class TimestampCapture : IDisposable
{
    readonly MMDevice device;
    readonly AudioClient client;
    readonly AudioCaptureClient capture;
    readonly List<float>[] samples;
    readonly int channel;
    long firstPosition, expectedPosition;
    bool first = true;
    public long StartQpc { get; private set; }
    public WaveFormat Format { get; }
    public string Error { get; private set; } = "";
    public TimestampCapture(string id, bool loopback, int selectedChannel = -1)
    {
        using var e = new MMDeviceEnumerator(); device = e.GetDevice(id); client = device.AudioClient;
        try
        {
        Format = client.MixFormat; channel = selectedChannel;
        if (Format.Channels > 16 || selectedChannel >= Format.Channels) throw new IOException("不支持的麦克风/参考声道格式。");
        samples = Enumerable.Range(0, selectedChannel >= 0 ? 1 : Format.Channels).Select(_ => new List<float>()).ToArray();
        client.Initialize(AudioClientShareMode.Shared, loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None, 2000000, 0, Format, Guid.Empty);
        capture = client.AudioCaptureClient; client.Start();
        }
        catch { capture?.Dispose(); client.Dispose(); device.Dispose(); throw; }
    }
    public void Drain()
    {
        while (capture.GetNextPacketSize() > 0)
        {
            var pointer = capture.GetBuffer(out int frames, out var flags, out long position, out long qpc);
            try
            {
                if (frames <= 0) return;
                if ((flags & AudioClientBufferFlags.TimestampError) != 0 || qpc <= 0) Error = "音频设备未提供可靠时间戳，不能计算延迟。";
                if (first) { StartQpc = qpc; firstPosition = position; first = false; }
                else if (position != expectedPosition || (flags & AudioClientBufferFlags.DataDiscontinuity) != 0) Error = "录音发生丢帧或不连续，请关闭高负载应用后重测。";
                double expectedQpc = StartQpc + (position - firstPosition) * 10000000.0 / Format.SampleRate;
                if (Math.Abs(qpc - expectedQpc) > 50000) Error = "设备音频时间戳漂移超过 5 ms，测量无效。";
                expectedPosition = position + frames;
                float[] data;
                if ((flags & AudioClientBufferFlags.Silent) != 0) data = new float[frames * Format.Channels];
                else { var bytes = new byte[frames * Format.BlockAlign]; Marshal.Copy(pointer, bytes, 0, bytes.Length); data = AudioEngine.Decode(bytes, bytes.Length, Format); }
                if (samples[0].Count > Format.SampleRate * 5) throw new IOException("录音超出测量时限。");
                for (int i = 0; i < frames; i++) for (int c = 0; c < samples.Length; c++) samples[c].Add(data[i * Format.Channels + (channel >= 0 ? channel : c)]);
            }
            finally { capture.ReleaseBuffer(frames); }
        }
    }
    public SignalMatch Find(bool bass, bool reference, CancellationToken cancellation)
    {
        if (Error.Length > 0) throw new IOException(Error);
        SignalMatch? best = null; string error = "没有收到录音数据。";
        foreach (var data in samples)
        {
            try
            {
                var match = LatencyAnalysis.Detect(LatencyAnalysis.Resample(data.ToArray(), Format.SampleRate), bass, reference, cancellation);
                if (best == null || match.Confidence > best.Confidence) best = match;
            }
            catch (IOException ex) { error = ex.Message; }
        }
        return best ?? throw new IOException(error);
    }
    public void Dispose() { try { client.Stop(); } finally { capture.Dispose(); client.Dispose(); device.Dispose(); } }
}

public sealed class CalibrationProbe(WaveFormat format, int channel, bool bass) : IWaveProvider
{
    int position;
    public WaveFormat WaveFormat { get; } = format;
    public int Read(byte[] buffer, int offset, int count)
    {
        var format = WaveFormat;
        if (format.BitsPerSample != 32 || !(format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible e && e.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"))) throw new IOException("测量需要浮点混音格式。");
        int frames = count / format.BlockAlign;
        var samples = new float[frames * format.Channels];
        for (int i = 0; i < frames; i++, position++) samples[i * format.Channels + channel] = .12f * LatencyAnalysis.Chirp((double)position / format.SampleRate - .35, bass);
        Buffer.BlockCopy(samples, 0, buffer, offset, samples.Length * 4); return samples.Length * 4;
    }
}

public sealed class CalibrationRun : IDisposable
{
    readonly CancellationTokenSource cancellation = new();
    readonly string root;
    readonly CalibrationCommand command;
    readonly Task task;
    string progress = "准备测量…";
    public string Id => command.RunId;
    public string Progress => Volatile.Read(ref progress);
    public bool Completed => task.IsCompleted;
    public CalibrationRun(string root, CalibrationCommand command)
    {
        this.root = root; this.command = command;
        task = Task.Run(Measure);
    }
    void Measure()
    {
        var rows = new List<LatencyRow>(); bool applied = false;
        CalibrationReport report;
        try
        {
            var profile = SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(command.Profile), AudioEngine.Devices());
            var source = AudioEngine.Devices().Single(d => d.Id == profile.SourceId);
            using (var e = new MMDeviceEnumerator())
            using (var d = e.GetDevice(command.MicrophoneId))
                if (d.DataFlow != DataFlow.Capture || d.State != DeviceState.Active) throw new IOException("请选择可用麦克风。");
            profile.Speakers = SpeakerLayouts.Resolve(profile, source);
            var active = profile.Speakers.Where(s => !s.Muted && s.DeviceId.Length > 0 && s.SourceChannel >= 0).ToList();
            if (active.Count < 2) throw new IOException("至少需要两只已配置且非静音的音箱。");
            if (active.GroupBy(s => s.SourceChannel).Any(g => g.Count() > 1)) throw new IOException("测量需要各音箱使用独立音源声道，不能重复映射同一音源。");
            foreach (var s in profile.Speakers) s.DelayMs = 0;
            foreach (var s in active) rows.Add(new LatencyRow { Role = s.Role });
            MeasureStage(profile, source, rows, false);
            LatencyAnalysis.Plan(rows);
            foreach (var s in profile.Speakers) { var row = rows.FirstOrDefault(r => r.Role == s.Role); if (row != null) s.DelayMs = row.CompensationMs; }
            MeasureStage(profile, source, rows, true);
            var verified = LatencyAnalysis.Verify(rows);
            cancellation.Token.ThrowIfCancellationRequested();
            var saved = ServiceFiles.Read(Path.Combine(root, "config.json"));
            if (saved.Revision != command.Revision) throw new IOException("测量期间配置已改变，未覆盖新配置。");
            var actual = AudioEngine.Devices().Single(d => d.Id == profile.SourceId);
            if (actual.Channels != source.Channels || actual.Mask != source.Mask || actual.Rate != source.Rate) throw new IOException("测量期间音源格式已改变，未保存补偿。");
            // Preserve every saved setting except the verified active-speaker delays.
            foreach (var s in saved.Profile.Speakers) { var row = rows.FirstOrDefault(r => r.Role == s.Role); if (row != null) s.DelayMs = row.CompensationMs; }
            ServiceFiles.AtomicWrite(Path.Combine(root, "config.json"), saved with { Revision = Guid.NewGuid().ToString("N") }); applied = true;
            report = new(command.RunId, true, true, $"验证通过，已自动应用补偿。复测平均延迟极差 {verified.MeanSpread:0.00} ms，中位数极差 {verified.MedianSpread:0.00} ms（均 ≤ 5 ms）。", rows, verified.MeanSpread, verified.MedianSpread);
        }
        catch (OperationCanceledException) { report = new(command.RunId, false, applied, "已取消测量，原补偿未改变。", rows); }
        catch (Exception ex) { report = new(command.RunId, false, applied, "测量/验证未通过，原补偿未改变：" + ex.Message, rows); }
        ServiceFiles.AtomicWrite(Path.Combine(root, "calibration-result.json"), report);
    }
    void MeasureStage(SurroundProfile profile, Endpoint source, List<LatencyRow> rows, bool verification)
    {
        using var routing = new AudioEngine(); string? fault = null; routing.Fault += s => Interlocked.Exchange(ref fault, s);
        routing.Start(source.Id, SpeakerLayouts.BuildRoutes(profile.Speakers, source, AudioEngine.Devices()), profile.FollowSourceVolume, profile.Buffers.QueueMs, profile.Buffers.CaptureMs, profile.Buffers.OutputMs);
        foreach (var row in rows)
        for (int repeat = 0; repeat < 3; repeat++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            Volatile.Write(ref progress, $"{(verification ? "补偿复测" : "初测")} · {row.Role} {SpeakerLayouts.Role(row.Role).Name} · 第 {repeat + 1}/3 次");
            LatencyTrial trial;
            try
            {
                if (fault != null) throw new IOException(fault);
                var speaker = profile.Speakers.Single(s => s.Role == row.Role);
                using var mic = new TimestampCapture(command.MicrophoneId, false);
                using var reference = new TimestampCapture(source.Id, true, speaker.SourceChannel);
                using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDevice(source.Id);
                using var formatClient = device.AudioClient;
                using var player = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
                player.Init(new CalibrationProbe(formatClient.MixFormat, speaker.SourceChannel, speaker.Role == "LFE")); player.Play();
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 2600)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    mic.Drain(); reference.Drain(); routing.UpdateSourceVolume();
                    Thread.Sleep(3);
                }
                player.Stop(); mic.Drain(); reference.Drain();
                if (Volatile.Read(ref fault) is string failure) throw new IOException(failure);
                var r = reference.Find(speaker.Role == "LFE", true, cancellation.Token);
                var m = mic.Find(speaker.Role == "LFE", false, cancellation.Token);
                double milliseconds = (mic.StartQpc - reference.StartQpc) / 10000.0 + (m.Seconds - r.Seconds) * 1000;
                if (!double.IsFinite(milliseconds) || milliseconds < 0 || milliseconds > 2000) throw new IOException("到达时间不在有效测量范围 0～2000 ms 内。");
                trial = new(milliseconds, m.Confidence);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { trial = new(null, 0, ex.Message); }
            (verification ? row.Verified : row.Initial).Add(trial);
        }
    }
    public void Cancel() => cancellation.Cancel();
    public void Dispose()
    {
        cancellation.Cancel();
        if (!task.Wait(TimeSpan.FromSeconds(8))) throw new IOException("测量线程未按时停止。");
        cancellation.Dispose();
    }
}
