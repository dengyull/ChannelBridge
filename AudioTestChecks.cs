using System.Text.Json;
namespace ChannelBridge;
public static class AudioTestChecks
{
    public static string Run(string outputDirectory)
    {
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
        var p = new SurroundProfile { SourceId = "source", LayoutId = "2.1", Speakers = SpeakerLayouts.Create(SpeakerLayouts.Get("2.1"), null) };
        string legacy = JsonSerializer.Serialize(p).Replace(",\"BufferMs\":80", "");
        Check(SpeakerLayouts.ReadProfile(legacy, Array.Empty<Endpoint>()).BufferMs == 80, "Old profiles retain standard buffer");
        var migrated = SpeakerLayouts.ReadProfile(legacy.Replace("\"FollowSourceVolume\":true", "\"FollowSourceVolume\":true,\"BufferMs\":40"), Array.Empty<Endpoint>());
        Check(migrated.Buffers == BufferSettings.LowLatency, "Legacy 40ms preset migrates to 50ms");
        var custom = new SurroundProfile { LayoutId = p.LayoutId, Speakers = p.Speakers, BufferMs = 65, CaptureBufferMs = 30, OutputBufferMs = 25 };
        Check(SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(custom), Array.Empty<Endpoint>()).Buffers == new BufferSettings(30, 65, 25), "All three custom buffers survive profile round-trip");
        custom.BufferMs = 40;
        Check(SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(custom), Array.Empty<Endpoint>()).BufferMs == 40, "Explicit custom 40ms is not migrated");
        custom.CaptureBufferMs = 0;
        bool invalidCapture = false;
        try { SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(custom), Array.Empty<Endpoint>()); } catch (ArgumentOutOfRangeException) { invalidCapture = true; }
        Check(invalidCapture, "Invalid custom capture buffer rejected");
        p.BufferMs = 50;
        Check(SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(p), Array.Empty<Endpoint>()).BufferMs == 50, "Low latency profile round-trip");
        bool rejectedBuffer = false;
        try { SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(p).Replace("\"BufferMs\":50", "\"BufferMs\":1"), Array.Empty<Endpoint>()); }
        catch (ArgumentOutOfRangeException) { rejectedBuffer = true; }
        Check(rejectedBuffer, "Unsupported buffer rejected");
        for (int i = 0; i < p.Speakers.Count; i++) { p.Speakers[i].SourceChannel = i; p.Speakers[i].DeviceId = "output-" + i; }
        p.Speakers.Single(s => s.Role == "FR").Muted = true;
        var now = DateTime.UtcNow;
        TestRequest Request(string? role = null) => new(Guid.NewGuid().ToString(), DateTime.UtcNow.AddSeconds(30), p, role);
        using (var seq = new AudioTestSequence(Request(), true))
        {
            Check(seq.Tick(now) && seq.Role == "FL" && seq.Count == 2, "Sequence first role / skip mute");
            Check(seq.Tick(now.AddSeconds(2)) && seq.Role == "LFE", "Sequence second role");
            Check(!seq.Tick(now.AddSeconds(4)), "Sequence completion");
        }
        using (var seq = new AudioTestSequence(Request("LFE"), true)) Check(seq.Tick(now) && seq.Count == 1 && seq.Role == "LFE", "Selected role");
        var s = p.Speakers[0]; s.Side = 1; s.DelayMs = 50; s.GainDb = -6;
        var signal = new TestSignal(48000, s); var bytes = new byte[48000 * 8]; int length = signal.Read(bytes, 0, bytes.Length);
        Check(length == 40800 * 8, "Tone duration plus configured delay");
        float peak = 0;
        for (int i = 0; i < length / 8; i++)
        {
            float l = BitConverter.ToSingle(bytes, i * 8), r = BitConverter.ToSingle(bytes, i * 8 + 4);
            Check(l == 0 && (i >= 2400 || r == 0), "Output isolation and delay"); peak = Math.Max(peak, Math.Abs(r));
        }
        Check(peak > .039f && peak < .041f, "Configured gain");
        foreach (var inputLayout in SpeakerLayouts.All)
        foreach (var targetLayout in SpeakerLayouts.All)
        {
            var ordered = inputLayout.Roles.OrderBy(r => SpeakerLayouts.Role(r).Bit).ToArray();
            var input = new Endpoint("source", inputLayout.Name, ordered.Length, 48000, ordered.Sum(r => SpeakerLayouts.Role(r).Bit));
            var profile = new SurroundProfile { LayoutId = targetLayout.Id, Speakers = targetLayout.Roles.Select(r => new SpeakerSetting { Role = r, SourceChannel = 63, DeviceId = "kept", GainDb = -7, Side = 1, DelayMs = 19 }).ToList() };
            var mapped = SpeakerLayouts.Resolve(profile, input);
            foreach (var output in mapped)
            {
                int expected = Array.IndexOf(ordered, output.Role);
                string fallback = output.Role switch { "BL" => "SL", "BR" => "SR", "SL" => "BL", "SR" => "BR", _ => "" };
                if (expected < 0 && fallback.Length > 0 && !targetLayout.Roles.Contains(fallback)) expected = Array.IndexOf(ordered, fallback);
                Check(output.SourceChannel == expected && output.GainDb == -7 && output.Side == 1 && output.DelayMs == 19 && output.DeviceId == "kept", $"Mapping {inputLayout.Id} -> {targetLayout.Id}: {output.Role}");
            }
            Check(profile.Speakers.All(x => x.SourceChannel == 63), "Runtime mapping must not overwrite saved draft");
            profile.AutoMatchSource = false; Check(SpeakerLayouts.Resolve(profile, input).All(x => x.SourceChannel == 63), "Manual mapping preserved");
        }
        var fifo = new AdaptiveStereo(48000, 48000, 0, 1, 1, 0);
        fifo.Push(Enumerable.Repeat(.2f, 20000).ToArray(), 10000, 2);
        var samples = new float[64]; fifo.MasterGain = .5f; fifo.Read(samples, 0, samples.Length);
        Check(samples.All(x => Math.Abs(x - .1f) < .0001f), "System master gain applies to output");
        fifo.MasterGain = 0; fifo.Read(samples, 0, samples.Length); Check(samples.All(x => x == 0), "Mute suppresses buffered audio");
        var mutedTest = new TestSignal(48000, s) { MasterGain = 0 }; int mutedBytes = mutedTest.Read(bytes, 0, bytes.Length);
        Check(Enumerable.Range(0, mutedBytes / 4).All(i => BitConverter.ToSingle(bytes, i * 4) == 0), "System mute also applies to test tone");
        string root = Path.Combine(outputDirectory, "ChannelBridge-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string configPath = Path.Combine(root, "config.json"), requestPath = Path.Combine(root, "test.json");
        var config = new ServiceConfig(true, p, "saved"); ServiceFiles.AtomicWrite(configPath, config);
        ServiceReport Report() => JsonSerializer.Deserialize<ServiceReport>(File.ReadAllText(Path.Combine(root, "status.json")))!;
        using (var worker = new RoutingWorker(root, true))
        {
            worker.Tick(); Check(Report().State == "Running", "Initial routing");
            ServiceFiles.AtomicWrite(requestPath, Request()); worker.Tick(); Check(Report().State == "Testing", "Pause routing for test");
            ServiceFiles.AtomicWrite(requestPath, Request() with { Cancel = true }); worker.Tick(); Check(Report().State == "Running", "Cancel restores routing");
            ServiceFiles.AtomicWrite(requestPath, Request() with { ExpiresUtc = DateTime.UtcNow.AddSeconds(-1) }); worker.Tick(); Check(Report().State == "Running", "Ignore expired test");
            ServiceFiles.AtomicWrite(requestPath, Request()); worker.Tick();
        }
        using (var worker = new RoutingWorker(root, true)) { worker.Tick(); Check(Report().State == "Running", "Restart does not replay old test"); Check(Report().BufferMs == 50, "Restart restores low latency mode"); }
        Check(ServiceFiles.Read(configPath).Revision == "saved" && ServiceFiles.Read(configPath).Enabled, "Tests preserve saved configuration");
        ServiceFiles.AtomicWrite(configPath, config with { Enabled = false, Revision = "stopped" });
        using (var worker = new RoutingWorker(root, true))
        {
            worker.Tick(); ServiceFiles.AtomicWrite(requestPath, Request("LFE")); worker.Tick(); Check(Report().State == "Testing", "Testing while routing stopped");
            Thread.Sleep(1500); worker.Tick(); Check(Report().State == "Stopped", "Completion preserves stopped intent");
        }
        return ServicePriorityChecks.Run(outputDirectory) + MultichannelChecks.Run() + "PASS: 169 source/target layout combinations, exact role indexes and limited side/back fallback\nPASS: automatic runtime remapping preserves physical settings; manual mode preserves indexes\nPASS: system volume scales audio and mute silences buffered output and test tone\nPASS: selected speaker; ordered sequence skips mute; completion\nPASS: output isolation, configured gain and delay\nPASS: test pauses routing; cancellation restores routing\nPASS: expired commands ignored; service restart never replays test\nPASS: saved profile preserved; stopped intent preserved after completion\nNOTE: playback lifecycle uses simulated devices; no audible test emitted.\n";
    }
}
