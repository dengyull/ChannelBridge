using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChannelBridge;
public static class AirPlayChecks
{
    public static string Run(string directory)
    {
        void Check(bool value, string label) { if (!value) throw new Exception(label); }
        // Independently generated with Python plistlib, including nested and UTF-16 values.
        var plist = Convert.FromBase64String("YnBsaXN0MDDaAQIDBAUGBwgJCgsPEBESExQVFhdcYXVkaW9Gb3JtYXRzWGRldmljZUlEWGZlYXR1cmVzVW1vZGVsVG5hbWVScGlScGtdc291cmNlVmVyc2lvbltzdGF0dXNGbGFnc1J2dqEM0Q0OVHR5cGUQYF8QETg2OjJhOjI4OmZlOjQ2OmI5ExI0VniavN7wWkFwcGxlVFYzLDJibUuL1Vd0ZXN0LXBpTxAgAAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh9WMjIwLjY4EAQQAggdKjM8QkdKTVtnamxvdHaKk56jq87V1wAAAAAAAAEBAAAAAAAAABgAAAAAAAAAAAAAAAAAAADZ");
        var metadata = BinaryPlist.Read(plist);
        Check((string)metadata["name"] == "测试" && (ulong)metadata["features"] == 0x123456789abcdef0UL, "Binary receiver metadata, Unicode and 64-bit features");
        var (air, raop) = AirPlayDiscovery.Properties(metadata);
        Check(air["features"] == "0x9ABCDEF0,0x12345678" && raop["ft"] == air["features"]
            && air["pk"] == Convert.ToHexString(Enumerable.Range(0,32).Select(i => (byte)i).ToArray()).ToLowerInvariant(), "Both DNS-SD records retain engine capabilities and public key");
        bool invalidPlist = false;
        try { BinaryPlist.Read(plist[..^9]); } catch (Exception) { invalidPlist = true; }
        Check(invalidPlist, "Reject truncated receiver metadata");
        string root = Path.Combine(directory, "airplay-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string engine = Path.Combine(root, "uxplay.exe"); File.WriteAllText(engine, "Test placeholder; never executed.");
        Directory.CreateDirectory(Path.Combine(root, "lib", "gstreamer-1.0"));
        string hash; using (var stream = File.OpenRead(engine)) hash = Convert.ToHexString(SHA256.HashData(stream));
        var settings = new AirPlaySettings(true, engine, hash, "ChannelBridge Test", "first"); settings.Validate(true);
        var info = AirPlayWorker.StartInfo(settings, "test-event", root);
        Check(!info.UseShellExecute && info.CreateNoWindow && info.ArgumentList.SequenceEqual(new[] { "-n", "ChannelBridge Test", "-nh", "-vs", "0", "-no-progress", "-m", AirPlayWorker.DeviceId(root), "-key", Path.Combine(root, "airplay-receiver.pem"), "-as", "wasapisink" }), "Audio-only fixed arguments and hidden process");
        Check(AirPlayWorker.AudioSink("{endpoint}") == "wasapisink device=\"{endpoint}\"", "Explicit routing-source output");
        Check(AirPlayWorker.AudioSink("a\\b\"c") == "wasapisink device=\"a\\\\b\\\"c\"", "GStreamer property escaping");
        Check(AirPlayWorker.DeviceId(root) == AirPlayWorker.DeviceId(root.ToUpperInvariant()) && AirPlayWorker.DeviceId(root) != AirPlayWorker.DeviceId(root + "-other"), "Stable, instance-specific receiver identity");
        Check((Convert.ToByte(AirPlayWorker.DeviceId(root).Split(':')[0], 16) & 3) == 2, "Locally administered unicast identity");
        Check(info.Environment["UXPLAYENHANCED_PARENT_PID"] == Environment.ProcessId.ToString(), "Receiver parent watchdog");
        Check(info.Environment["GST_DEBUG"] == "2" && info.Environment["GST_DEBUG_NO_COLOR"] == "1", "Bounded warning-level GStreamer diagnostics");
        Check(AirPlayWorker.ShouldLog("ct=2 spf=352 usingScreen=0 isMedia=1 audioFormat=0x40000")
            && AirPlayWorker.ShouldLog("audio quality: codec=ALAC") && !AirPlayWorker.ShouldLog("Title: private song"), "Keep negotiated formats without general media metadata");
        string config = Path.Combine(root, "airplay.json");
        AirPlayReport Report() => JsonSerializer.Deserialize<AirPlayReport>(File.ReadAllText(Path.Combine(root, "airplay-status.json")))!;
        int launches = 0;
        string? lastSink = null;
        ProcessStartInfo Fake(AirPlaySettings s, string stop, string data)
        {
            launches++;
            var start = AirPlayWorker.StartInfo(s, stop, data);
            lastSink = start.ArgumentList.Last();
            start.FileName = Environment.ProcessPath!; start.ArgumentList.Clear(); start.ArgumentList.Add("--receiver-test-child"); return start;
        }
        using (var worker = new AirPlayWorker(root, Fake))
        {
            worker.Tick(); Check(launches == 0 && Report().State == "Disabled", "No AirPlay process when disabled");
            ServiceFiles.AtomicWrite(config, settings); worker.Tick();
            Check(Report().State == "Running" && launches == 1, "Receiver process start");
            using var child = Process.GetProcessById(Report().ProcessId);
            worker.Tick(); Check(launches == 1, "Stable receiver is not restarted");
            ServiceFiles.AtomicWrite(config, settings with { Enabled = false, Revision = "off" }); worker.Tick();
            Check(child.WaitForExit(3000) && Report().State == "Disabled", "Disable signals receiver and leaves no process");
            ServiceFiles.AtomicWrite(config, settings with { Revision = "restart" }); worker.Tick();
            using var restarted = Process.GetProcessById(Report().ProcessId);
            File.WriteAllText(config, "{"); worker.Tick();
            Check(restarted.WaitForExit(3000) && Report().State == "Error", "Corrupt AirPlay settings stop the receiver");
        }
        string routePath = Path.Combine(root, "config.json");
        var route = new SurroundProfile { SourceId = "source-a", LayoutId = "2.0", Speakers = SpeakerLayouts.Create(SpeakerLayouts.Get("2.0"), null) };
        ServiceFiles.AtomicWrite(routePath, new ServiceConfig(true, route, "route-a"));
        ServiceFiles.AtomicWrite(config, settings with { Revision = "output-test" });
        using (var worker = new AirPlayWorker(root, Fake))
        {
            worker.Tick();
            Check(Report().State == "Running" && lastSink == AirPlayWorker.AudioSink("source-a"), "AirPlay uses saved routing source");
            using var oldChild = Process.GetProcessById(Report().ProcessId);
            route.SourceId = "source-b";
            ServiceFiles.AtomicWrite(routePath, new ServiceConfig(true, route, "route-b"));
            worker.Tick();
            Check(oldChild.WaitForExit(3000) && Report().State == "Running" && lastSink == AirPlayWorker.AudioSink("source-b"), "Changing routing source restarts receiver with new destination");
            int savedLaunches = launches;
            route.Speakers[0].GainDb = -6;
            ServiceFiles.AtomicWrite(routePath, new ServiceConfig(true, route, "gain-only"));
            worker.Tick(); Check(launches == savedLaunches, "Unrelated routing edits do not interrupt AirPlay");
        }
        File.AppendAllText(engine, "changed"); ServiceFiles.AtomicWrite(config, settings);
        using (var worker = new AirPlayWorker(root, Fake)) { worker.Tick(); Check(Report().State == "Error" && launches == 4, "Changed engine is rejected before launch"); }
        string eventName = "Local\\ChannelBridge-JobTest-" + Guid.NewGuid().ToString("N");
        using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var childInfo = Fake(settings, eventName, root);
        using var jobChild = Process.Start(childInfo)!;
        using (var job = new ReceiverJob()) job.Assign(jobChild);
        Check(jobChild.WaitForExit(3000), "Job close terminates receiver on service crash");
        return "PASS: AirPlay default-off, fixed audio-only arguments, engine hash validation, persistent configuration, real child-process start/stop and job cleanup\nNOTE: helper process only; no AirPlay engine or phone playback tested.\n";
    }
}

