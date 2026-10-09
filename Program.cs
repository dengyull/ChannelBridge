using System.Text.Json;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace ChannelBridge;
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--service") { System.ServiceProcess.ServiceBase.Run(new AudioWindowsService()); return; }
        if (args.Length == 2 && args[0] == "--service-host-test") { RoutingWorker.Run(args[1], true, CancellationToken.None).GetAwaiter().GetResult(); return; }
        ApplicationConfiguration.Initialize();
        UiLanguage.Load();
        if (args.Contains("--english")) UiLanguage.Set(true, false);
        if (args.Contains("--chinese")) UiLanguage.Set(false, false);
        if (args.Length >= 2 && args[0] == "--buffer-ui-check")
        {
            using var form = new BufferSettingsForm(new BufferSettings(30, 65, 25));
            form.Show(); Application.DoEvents();
            if (form.Settings != new BufferSettings(30, 65, 25)) throw new Exception("Custom buffer dialog changed values.");
            using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "--latency-check")
        {
            try { File.WriteAllText(args[1], LatencyChecks.Run(Path.GetDirectoryName(Path.GetFullPath(args[1]))!)); }
            catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length >= 2 && args[0] == "--calibration-ui-check")
        {
            using var form = new CalibrationForm(new SurroundProfile(), true);
            form.Show(); Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(args[1]); return;
        }
        if (args.Length == 2 && args[0] == "--apply-saved-profile")
        {
            try
            {
                var saved = ServiceFiles.ReadSaved() ?? throw new Exception("No saved profile");
                ProfileApplication.Apply(new ApplyRequest(saved.Profile, null));
                var actual = AudioEngine.Devices().Single(d => d.Id == saved.Profile.SourceId);
                File.WriteAllText(args[1], JsonSerializer.Serialize(new { Applied = true, saved.Profile.LayoutId, actual.Channels, actual.Mask, PhysicalSpeakers = WindowsSpeakerMode.ReadSpeakers(actual.Id), Enabled = ServiceFiles.ReadSaved()!.Enabled }));
            }
            catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 3 && args[0] == "--apply-windows-profile")
        {
            string result = ProfileApplication.ResultPath(args[2]);
            try
            {
                if (args[1].Length > 32768) throw new Exception("配置过大。");
                var request = JsonSerializer.Deserialize<ApplyRequest>(Convert.FromBase64String(args[1])) ?? throw new Exception("配置为空。");
                ProfileApplication.ApplyCore(request); File.WriteAllText(result, JsonSerializer.Serialize(""));
            }
            catch (Exception ex) { File.WriteAllText(result, JsonSerializer.Serialize(ex.Message)); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--windows-layout-check")
        {
            try
            {
                var saved = ServiceFiles.ReadSaved() ?? throw new Exception("No saved profile");
                var before = AudioEngine.Devices().Single(d => d.Id == saved.Profile.SourceId);
                if (!before.Name.Contains("CABLE Input")) throw new Exception("Diagnostic limited to CABLE Input");
                var result = new List<string>();
                foreach (string layoutId in SpeakerLayouts.All.Select(l => l.Id))
                {
                    using (var change = new WindowsSpeakerMode(before.Id, SpeakerLayouts.Get(layoutId)))
                    {
                        using var e = new MMDeviceEnumerator(); using var device = e.GetDevice(before.Id);
                        var physical = device.Properties[PropertyKeys.PKEY_AudioEndpoint_PhysicalSpeakers].Value;
                        if (Convert.ToUInt32(physical) != WindowsSpeakerMode.Mask(SpeakerLayouts.Get(layoutId))) throw new Exception("Windows speaker configuration mismatch");
                        result.Add($"{layoutId}: Channels={change.Actual.Channels}; MixMask={change.Actual.Mask}; PhysicalSpeakers={physical}");
                    }
                    Thread.Sleep(200);
                }
                var restored = AudioEngine.Devices().Single(d => d.Id == before.Id);
                if (restored.Channels != before.Channels || restored.Mask != before.Mask) throw new Exception("Original format was not restored");
                result.Add("PASS: original format restored; saved route configuration unchanged."); File.WriteAllLines(args[1], result);
            }
            catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--volume-check")
        {
            try
            {
                var config = ServiceFiles.ReadSaved() ?? throw new Exception("No saved profile");
                using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDevice(config.Profile.SourceId);
                if (!device.FriendlyName.Contains("CABLE Input")) throw new Exception("Diagnostic limited to CABLE Input");
                var volume = device.AudioEndpointVolume; float saved = volume.MasterVolumeLevelScalar; bool mute = volume.Mute;
                try
                {
                    float reduced = saved * .5f; volume.MasterVolumeLevelScalar = reduced;
                    Thread.Sleep(1600);
                    var report = JsonSerializer.Deserialize<ServiceReport>(File.ReadAllText(Path.Combine(ServiceFiles.Root, "status.json")))!;
                    if (report.Version != typeof(Program).Assembly.GetName().Version!.ToString(3) || report.State != "Running" || Math.Abs(report.SourceGain - (mute ? 0 : reduced)) > .002) throw new Exception("Service did not follow endpoint volume");
                    volume.Mute = true; Thread.Sleep(1600);
                    report = JsonSerializer.Deserialize<ServiceReport>(File.ReadAllText(Path.Combine(ServiceFiles.Root, "status.json")))!;
                    if (report.SourceGain != 0) throw new Exception("Service did not follow mute");
                    File.WriteAllText(args[1], $"PASS: actual service followed source endpoint scalar {saved:0.000} -> {reduced:0.000} and mute -> zero. Original volume/mute restored in finally. No test audio emitted.");
                }
                finally { volume.MasterVolumeLevelScalar = saved; volume.Mute = mute; }
            }
            catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--audio-test-check")
        {
            try { File.WriteAllText(args[1], AudioTestChecks.Run(Path.GetDirectoryName(Path.GetFullPath(args[1]))!)); }
            catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--install-service")
        {
            try { ServiceSetup.Install(args[1]); }
            catch (Exception ex) { UiMessage.Show(ex.ToString(), "ChannelBridge 服务安装失败"); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length >= 2 && args[0] == "--ui-test")
        {
            try { using var f = new MainForm(); File.WriteAllText(args[1], f.VerifyUi()); }
            catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length >= 2 && args[0] == "--backend-check")
        {
            try
            {
                var endpoints = AudioEngine.Devices();
                var input = endpoints.First(e => e.Channels >= 4);
                var outputs = endpoints.Where(e => e.Id != input.Id && e.Channels == 2).Take(2).ToList();
                if (outputs.Count < 2) throw new Exception("Need two stereo endpoints for the backend check.");
                using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDevice(input.Id);
                using var silence = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
                using var formatClient = device.AudioClient;
                silence.Init(new ZeroProvider(formatClient.MixFormat)); silence.Play();
                using var engine = new AudioEngine(); string? fault = null; engine.Fault += e => fault = e;
                engine.Start(input.Id, outputs.Select((e, i) => new Route(e.Id, i * 2, i * 2 + 1, 0, 0)).ToList());
                Thread.Sleep(1500);
                if (fault != null) throw new Exception(fault);
                if (Interlocked.Read(ref engine.FramesCaptured) == 0) throw new Exception("No loopback frames received.");
                var report = new { Status = "PASS", SourceChannels = engine.SourceChannels, Frames = engine.FramesCaptured,
                    OutputCount = engine.Buffers.Count, BuffersMs = engine.Buffers.Select(b => b.BufferedMs).ToArray(),
                    Underruns = engine.Buffers.Select(b => b.Underruns).ToArray(), Note = "All output gains were zero. No test sound or captured samples saved. This validates backend startup, not acoustic synchronization." };
                File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length >= 2 && args[0] == "--devices")
        {
            try { File.WriteAllText(args[1], JsonSerializer.Serialize(AudioEngine.Devices(), new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length >= 2 && args[0] == "--self-test")
        {
            try { File.WriteAllText(args[1], SelfTest.Run()); }
            catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length >= 2 && args[0] == "--volume-ui-check") { DeviceVolumeControl.RenderExample(args[1]); return; }
        if (args.Length >= 2 && args[0] == "--ui-check")
        {
            using var f = new MainForm(); f.PreparePreview();
            if (args.Length >= 4) f.Size = new Size(int.Parse(args[2]), int.Parse(args[3]));
            if (args.Length >= 5) f.PreviewLayout(args[4]);
            if (args.Contains("--native-preview")) f.PrepareMultichannelPreview();
            f.Show(); Application.DoEvents();
            using var b = new Bitmap(f.Width, f.Height); f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height)); b.Save(args[1]); f.Close(); return;
        }
        Application.Run(new MainForm());
    }
}

static class SelfTest
{
    public static string Run()
    {
        var passed = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); passed.Add("PASS: " + name); }
        float[] src = new float[4800 * 6];
        for (int f = 0; f < 4800; f++) for (int c = 0; c < 6; c++) src[f * 6 + c] = (c + 1) * .1f;
        var fifo = new AdaptiveStereo(48000, 44100, 4, 1, .5f, 0);
        fifo.Push(src, 4800, 6); var dst = new float[882]; fifo.Read(dst, 0, dst.Length);
        Check(dst.Where((_, i) => i % 2 == 0).All(x => Math.Abs(x - .25f) < 1e-5), "6-channel -> stereo left mapping, gain and 48k/44.1k conversion");
        Check(dst.Where((_, i) => i % 2 == 1).All(x => Math.Abs(x - .1f) < 1e-5), "independent right channel mapping");
        var mute = new AdaptiveStereo(48000, 48000, -1, 2, 1, 0); mute.Push(src, 4800, 6); mute.Read(dst, 0, dst.Length);
        Check(dst.Where((_, i) => i % 2 == 0).All(x => x == 0), "unmapped channel is silent");
        var delayed = new AdaptiveStereo(48000, 48000, 0, 1, 1, 100); delayed.Push(src, 4800, 6); delayed.Read(dst, 0, dst.Length);
        Check(dst.All(x => x == 0), "delay prebuffer emits silence");
        for (int i = 0; i < 100; i++) fifo.Push(src, 4800, 6);
        Check(fifo.Overruns > 0 && fifo.BufferedMs < 3000, "overflow is bounded and recovers");
        for (int i = 0; i < 1000; i++) mute.Read(dst, 0, dst.Length);
        Check(mute.Underruns > 0 && dst.All(x => x == 0), "underflow emits silence instead of stale audio");
        var pcm = AudioEngine.Decode(new byte[] { 0, 128, 255, 127 }, 4, new WaveFormat(48000, 16, 1));
        Check(pcm[0] == -1 && pcm[1] > .999, "PCM16 signed decode");
        var pcm24 = AudioEngine.Decode(new byte[] { 0, 0, 128, 255, 255, 127 }, 6, new WaveFormat(48000, 24, 1));
        Check(pcm24[0] == -1 && pcm24[1] > .999, "PCM24 sign extension");
        var endpoint = new Endpoint("test", "5.1", 6, 48000, 63);
        Check(SpeakerLayouts.Create(SpeakerLayouts.Get("5.0"), endpoint).Select(s => s.SourceChannel).SequenceEqual(new[] { 0, 1, 2, 4, 5 }), "5.0 preset skips LFE");
        Check(SpeakerLayouts.Create(SpeakerLayouts.Get("4.0"), endpoint).Select(s => s.SourceChannel).SequenceEqual(new[] { 0, 1, 4, 5 }), "4.0 uses front and rear");
        var drift = new AdaptiveStereo(48000, 48000, 0, 1, 1, 0);
        drift.Push(src, 4800, 6); float[] chunk = new float[481 * 2];
        float[] sourceChunk = new float[480 * 2];
        for (int i = 0; i < 20000; i++) { drift.Push(sourceChunk, 480, 2); drift.Read(chunk, 0, chunk.Length); }
        Check(drift.Underruns == 0 && drift.Overruns == 0 && drift.BufferedMs > 15 && drift.BufferedMs < 200,
            $"200-second simulated independent clock (+2083 ppm) stays bounded: underruns={drift.Underruns}, overruns={drift.Overruns}, buffer={drift.BufferedMs:0.0}ms");
        foreach (int outputFrames in new[] { 479, 481 })
        {
            var low = new AdaptiveStereo(48000, 48000, 0, 1, 1, 0, bufferMs: 50);
            low.Push(new float[4800 * 2], 4800, 2);
            var readBlock = new float[outputFrames * 2];
            for (int i = 0; i < 20000; i++) { low.Push(sourceChunk, 480, 2); low.Read(readBlock, 0, readBlock.Length); }
            Check(low.Underruns == 0 && low.Overruns == 0 && low.BufferedMs > 5 && low.BufferedMs < 80,
                $"50ms buffer: 200-second drift at {outputFrames} output frames remains bounded");
        }
        var lowDelay = new AdaptiveStereo(48000, 48000, 0, 1, 1, 91, 1, 0, 50);
        var jitterPacket = new float[960 * 2];
        lowDelay.Push(new float[9600 * 2], 9600, 2);
        var jitterOutput = new float[480 * 2];
        for (int i = 0; i < 10000; i++)
        {
            // One capture wakeup is delayed by 10ms, then the accumulated packet arrives.
            if (i % 100 != 50) lowDelay.Push(jitterPacket, i % 100 == 51 ? 960 : 480, 2);
            lowDelay.Read(jitterOutput, 0, jitterOutput.Length);
        }
        Check(lowDelay.Underruns == 0 && lowDelay.Overruns == 0, "50ms buffer survives 10ms capture jitter with unequal 91/0ms speaker compensation");
        var startLow = new AdaptiveStereo(48000, 48000, 0, 1, 1, 0, bufferMs: 50);
        var startNormal = new AdaptiveStereo(48000, 48000, 0, 1, 1, 0);
        var constant = Enumerable.Repeat(.25f, 2880 * 2).ToArray();
        startLow.Push(constant, 2880, 2); startNormal.Push(constant, 2880, 2);
        var firstSample = new float[2]; startLow.Read(firstSample, 0, 2);
        Check(firstSample[0] > .2f, "50ms mode primes before standard 80ms mode");
        startNormal.Read(firstSample, 0, 2); Check(firstSample[0] == 0, "Standard 80ms startup threshold preserved");
        var tone = new TestTone(48000, 1); var samples = new float[4800]; tone.Read(samples, 0, samples.Length);
        Check(samples.Where((_, i) => i % 2 == 0).All(x => x == 0) && samples.Any(x => x > .01), "test tone only reaches requested side");
        foreach (var layout in SpeakerLayouts.All)
        {
            int mask = layout.Roles.Sum(r => SpeakerLayouts.Role(r).Bit);
            var input = new Endpoint("source", layout.Name, layout.Roles.Length, 48000, mask);
            var mapping = SpeakerLayouts.Create(layout, input);
            Check(mapping.Select(s => s.SourceChannel).Order().SequenceEqual(Enumerable.Range(0, layout.Roles.Length)), layout.Id + " maps every channel from its actual Windows mask");
        }
        var seven = new Endpoint("source", "7.1", 8, 48000, 0x63f);
        var twoOne = SpeakerLayouts.Create(SpeakerLayouts.Get("2.1"), seven);
        Check(twoOne.Select(s => s.SourceChannel).SequenceEqual(new[] { 0, 1, 3 }), "2.1 takes LFE, not center");
        var stereo = new Endpoint("stereo", "Stereo", 2, 48000, 3);
        Check(SpeakerLayouts.Match(stereo, SpeakerLayouts.Get("2.1"), "LFE") == -1, "missing LFE is left unconnected");
        Check(SpeakerLayouts.Match(endpoint, SpeakerLayouts.Get("5.1"), "SL") == 4 && SpeakerLayouts.Match(endpoint, SpeakerLayouts.Get("7.1"), "SL") == -1, "side/back fallback is limited to single surround pair layouts");
        var outputs = Enumerable.Range(0, 4).Select(i => new Endpoint("out" + i, "Stereo " + i, 2, 48000, 3)).ToList();
        var all = SpeakerLayouts.Create(SpeakerLayouts.Get("7.1"), seven);
        for (int i = 0; i < all.Count; i++) { all[i].DeviceId = outputs[i / 2].Id; all[i].Side = i % 2; all[i].GainDb = 0; }
        var routes = SpeakerLayouts.BuildRoutes(all, seven, outputs);
        Check(routes.Count == 4 && routes.SelectMany(r => new[] { r.Left, r.Right }).Order().SequenceEqual(Enumerable.Range(0, 8)), "7.1 builds four distinct stereo streams without losing a channel");
        float[] eight = new float[4800 * 8]; for (int f = 0; f < 4800; f++) for (int c = 0; c < 8; c++) eight[f * 8 + c] = .1f * (c + 1);
        foreach (var r in routes)
        {
            var b = new AdaptiveStereo(48000, 48000, r.Left, r.Right, r.Gain, r.DelayMs, r.RightGain, r.RightDelayMs); b.Push(eight, 4800, 8); b.Read(dst, 0, dst.Length);
            Check(Math.Abs(dst[0] - (r.Left + 1) * .1f) < 1e-5 && Math.Abs(dst[1] - (r.Right + 1) * .1f) < 1e-5, "7.1 PCM reaches correct L/R for " + r.DeviceId);
        }
        all[1].Side = 0; bool blocked = false; try { SpeakerLayouts.BuildRoutes(all, seven, outputs); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked, "duplicate physical output side is rejected"); all[1].Side = 1;
        var ramp = Enumerable.Range(0, 10000).Select(i => i / 20000f).ToArray();
        var independent = new AdaptiveStereo(48000, 48000, 0, 0, .5f, 0, .25f, 20); independent.Push(ramp, ramp.Length, 1); independent.Read(dst, 0, 2);
        Check(Math.Abs((dst[0] / .5f - dst[1] / .25f) - .048f) < 1e-5, "independent L/R gain and 20ms relative delay");
        var profile = new SurroundProfile { SourceId = seven.Id, LayoutId = "7.1", Speakers = all }; all[3].GainDb = -6; all[3].DelayMs = 35; all[3].Muted = true;
        var loaded = SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(profile), outputs);
        Check(loaded.Speakers[3].GainDb == -6 && loaded.Speakers[3].DelayMs == 35 && loaded.Speakers[3].Muted && loaded.Speakers.Count == 8, "v2 profile round-trip preserves per-speaker properties");
        var legacy = new Profile(seven.Id, 5, new List<Route> { new("out0", 0, 1, .5f, 12), new("out1", 4, 5, .75f, 0), new("out2", 2, -1, 1, 5) });
        var migrated = SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(legacy), outputs);
        Check(migrated.LayoutId == "5.0" && migrated.Speakers.First(s => s.Role == "FC").SourceChannel == 2 && Math.Abs(migrated.Speakers[0].LinearGain - .5f) < 1e-5, "v1 five-channel profile migration preserves actual routing and gain");
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f, 2.25f, 3f })
        {
            using var map = new SpeakerMap { TestScale = scale, Size = new Size((int)(520 * scale), (int)(340 * scale)), Source = seven };
            using var bitmap = new Bitmap(map.Width, map.Height);
            foreach (var layout in SpeakerLayouts.All)
            {
                map.Speakers = SpeakerLayouts.Create(layout, seven); map.DrawToBitmap(bitmap, new Rectangle(0, 0, map.Width, map.Height)); map.VerifyGeometry();
                foreach (var s in map.Speakers) { map.SelectAt(map.SpeakerCenter(s.Role)); if (map.SelectedRole != s.Role) throw new Exception("Scaled hit testing failed"); }
            }
            Check(true, $"all layouts draw and hit-test at {scale * 100:0}% diagram scale");
        }
        return string.Join(Environment.NewLine, passed) + Environment.NewLine;
    }
}

sealed class ZeroProvider(WaveFormat format) : IWaveProvider
{
    public WaveFormat WaveFormat => format;
    public int Read(byte[] buffer, int offset, int count) { Array.Clear(buffer, offset, count); return count; }
}
