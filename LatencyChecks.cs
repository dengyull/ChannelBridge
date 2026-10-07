namespace ChannelBridge;

public static class LatencyChecks
{
    static List<LatencyTrial> Trials(params double[] values) => values.Select(v => new LatencyTrial(v, .99)).ToList();
    public static List<LatencyRow> Example() => new()
    {
        new() { Role = "FL", Initial = Trials(81, 80, 82), Verified = Trials(110, 111, 109), CompensationMs = 29 },
        new() { Role = "FR", Initial = Trials(85, 84, 86), Verified = Trials(111, 110, 112), CompensationMs = 25 },
        new() { Role = "BL", Initial = Trials(110, 111, 109), Verified = Trials(110, 109, 111) },
        new() { Role = "BR", Initial = Trials(108, 109, 107), Verified = Trials(111, 110, 109), CompensationMs = 2 }
    };
    public static string Run(string workspace)
    {
        var results = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); results.Add("PASS: " + name); }
        void Reject(Action action, string name) { bool rejected = false; try { action(); } catch (IOException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } Check(rejected, name); }
        float[] Signal(int rate, double arrival, bool bass, double noise = .001, double polarity = 1)
        {
            var random = new Random(739); var data = new float[(int)(rate * 2.6)];
            for (int i = 0; i < data.Length; i++) data[i] = (float)(polarity * .12 * LatencyAnalysis.Chirp((double)i / rate - arrival, bass) + noise * (random.NextDouble() * 2 - 1));
            return LatencyAnalysis.Resample(data, rate);
        }
        foreach (int rate in new[] { 44100, 48000, 96000 })
        foreach (bool bass in new[] { false, true })
        {
            var reference = LatencyAnalysis.Detect(Signal(rate, .35, bass), bass, true);
            var mic = LatencyAnalysis.Detect(Signal(rate, .472, bass, .002, -1), bass, false);
            // Microphone started 7 ms before the source capture: common QPC removes that offset.
            double latency = -7 + (mic.Seconds - reference.Seconds) * 1000;
            Check(Math.Abs(latency - 115) < .3, $"{rate} Hz bass={bass}: QPC-adjusted known delay 115 ms -> {latency:0.000} ms");
        }
        Check(LatencyAnalysis.Stable(Trials(100, 105, 110)), "10 ms stability boundary accepted");
        Check(!LatencyAnalysis.Stable(Trials(100, 105, 110.01)), "above 10 ms stability boundary rejected");
        var rows = Example(); LatencyAnalysis.Plan(rows);
        Check(rows[0].Median == 81 && rows[0].CompensationMs == 29 && rows[2].CompensationMs == 0, "median independent of trial order; slowest speaker sets zero compensation");
        var verified = LatencyAnalysis.Verify(rows); Check(verified.MeanSpread <= 1, "three verification means and medians align");
        rows[0].Initial = Trials(80, 81, 100); Reject(() => LatencyAnalysis.Plan(rows), "unstable initial triplet rejected");
        rows = Example(); rows[0].Initial.RemoveAt(0); Reject(() => LatencyAnalysis.Plan(rows), "missing trial rejected");
        rows = Example(); rows[0].Initial = Trials(double.NaN, 80, 81); Reject(() => LatencyAnalysis.Plan(rows), "non-finite trial rejected");
        rows = Example(); rows[0].Initial = Trials(800, 801, 799); Reject(() => LatencyAnalysis.Plan(rows), "compensation above 500 ms rejected");
        rows = Example(); rows[0].Verified = Trials(120, 121, 122); Reject(() => LatencyAnalysis.Verify(rows), "stable but misaligned means rejected");
        rows = Example(); rows[0].Verified = Trials(104, 112, 115); Reject(() => LatencyAnalysis.Verify(rows), "unstable verification rejected");
        Reject(() => LatencyAnalysis.Detect(new float[31200], false, false), "silence rejected");
        Reject(() => LatencyAnalysis.Detect(Signal(48000, .4, false, .3), false, false), "low signal-to-noise rejected");
        var clipped = Signal(48000, .4, false); Array.Fill(clipped, 1f, 3000, 500);
        Reject(() => LatencyAnalysis.Detect(clipped, false, false), "clipping rejected");
        var echo = Signal(48000, .4, false, 0); var second = Signal(48000, .9, false, 0);
        for (int i = 0; i < echo.Length; i++) echo[i] += second[i];
        Reject(() => LatencyAnalysis.Detect(echo, false, false), "ambiguous equal arrival peaks rejected");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); bool cancelled = false;
        try { LatencyAnalysis.Detect(Signal(48000, .4, false), false, false, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "analysis cancellation honored");
        string root = Path.Combine(Path.GetFullPath(workspace), "ChannelBridge-latency-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profile = new SurroundProfile { SourceId = "test-source", LayoutId = "4.0", Speakers = SpeakerLayouts.Create(SpeakerLayouts.Get("4.0"), null) };
            string configPath = Path.Combine(root, "config.json"), requestPath = Path.Combine(root, "calibration-request.json"), resultPath = Path.Combine(root, "calibration-result.json");
            ServiceFiles.AtomicWrite(configPath, new ServiceConfig(true, profile, "test-revision"));
            string original = File.ReadAllText(configPath);
            using (var worker = new RoutingWorker(root, true))
            {
                worker.Tick();
                ServiceFiles.AtomicWrite(requestPath, new CalibrationCommand("one", "one", DateTime.UtcNow.AddSeconds(30), "fake-mic", "test-revision", profile));
                worker.Tick();
                var result = System.Text.Json.JsonSerializer.Deserialize<CalibrationReport>(File.ReadAllText(resultPath))!;
                Check(!result.Applied && !result.Success && File.ReadAllText(configPath) == original, "failed calibration request leaves saved compensation and enabled intent unchanged");
                Check(System.Text.Json.JsonSerializer.Deserialize<ServiceReport>(File.ReadAllText(Path.Combine(root, "status.json")))!.State == "Running", "routing survives rejected measurement");
            }
            File.Delete(resultPath);
            using (var worker = new RoutingWorker(root, true))
            {
                worker.Tick(); Check(!File.Exists(resultPath), "service restart does not replay old microphone request");
                ServiceFiles.AtomicWrite(requestPath, new CalibrationCommand("expired", "expired", DateTime.UtcNow.AddSeconds(-1), "fake-mic", "test-revision", profile));
                worker.Tick(); Check(!File.Exists(resultPath), "expired microphone request ignored");
            }
        }
        finally { Directory.Delete(root, true); }
        results.Add("Synthetic signals only: no microphone opened, no audio emitted. Acoustic accuracy remains untested.");
        return string.Join(Environment.NewLine, results);
    }
}
