using System.Diagnostics;
using System.Text.Json;

namespace ChannelBridge;
public static class ServicePriorityChecks
{
    public static string Run(string directory)
    {
        void Check(bool value, string label) { if (!value) throw new Exception(label); }
        var profile = new SurroundProfile { LayoutId = "2.0", Speakers = SpeakerLayouts.Create(SpeakerLayouts.Get("2.0"), null) };
        var legacy = JsonSerializer.Serialize(profile).Replace(",\"ServicePriority\":\"Normal\"", "");
        Check(SpeakerLayouts.ReadProfile(legacy, Array.Empty<Endpoint>()).ServicePriority == "Normal", "Priority legacy default");
        foreach (var option in ServicePriority.Options)
        {
            profile.ServicePriority = option.Id;
            Check(SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(profile), Array.Empty<Endpoint>()).ServicePriority == option.Id, "Priority round trip");
        }
        profile.ServicePriority = "RealTime";
        bool rejected = false;
        try { SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(profile), Array.Empty<Endpoint>()); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Unsupported priority rejected");
        string root = Path.Combine(directory, "priority-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "config.json");
        profile.ServicePriority = "AboveNormal";
        var config = new ServiceConfig(false, profile, "first"); ServiceFiles.AtomicWrite(path, config);
        var applied = new List<string>(); bool fail = false;
        void Apply(string value) { if (fail) throw new IOException("Injected priority failure"); applied.Add(value); }
        using (var worker = new RoutingWorker(root, true, Apply))
        {
            worker.Tick(); worker.Tick(); Check(applied.SequenceEqual(new[] { "AboveNormal" }), "Apply once per revision");
            profile.ServicePriority = "High"; config = config with { Revision = "second" }; ServiceFiles.AtomicWrite(path, config);
            fail = true; worker.Tick();
            var status = JsonSerializer.Deserialize<ServiceReport>(File.ReadAllText(Path.Combine(root, "status.json")))!;
            Check(status.Revision == "first" && status.Message.Contains("Injected priority failure"), "Failure preserves prior configuration and is reported");
            fail = false; worker.Tick(); Check(applied.Last() == "High", "Priority retry");
        }
        using (var worker = new RoutingWorker(root, true, Apply)) worker.Tick();
        Check(applied.SequenceEqual(new[] { "AboveNormal", "High", "High" }), "Restart reapplies persisted priority");
        using var process = Process.GetCurrentProcess(); var original = process.PriorityClass;
        try { ServicePriority.Apply("BelowNormal"); process.Refresh(); Check(process.PriorityClass == ProcessPriorityClass.BelowNormal, "Actual process priority"); ServicePriority.Apply("Normal"); }
        finally { process.PriorityClass = original; }
        return "PASS: service priority migration, persistence, restart, failure/retry and actual Windows process priority\n";
    }
}
