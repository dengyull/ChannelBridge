using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChannelBridge;

public sealed record AirPlaySettings(bool Enabled, string EnginePath, string EngineSha256, string ReceiverName, string Revision)
{
    public static AirPlaySettings Disabled => new(false, "", "", "ChannelBridge", "default");
    public void Validate(bool checkFiles = false)
    {
        if (string.IsNullOrWhiteSpace(ReceiverName) || ReceiverName.Length > 63 || ReceiverName.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(Revision) || Revision.Length > 64) throw new InvalidDataException("AirPlay 配置无效。");
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(EnginePath) || EnginePath.Length > 1024 || !Path.IsPathFullyQualified(EnginePath)
            || !string.Equals(Path.GetFileName(EnginePath), "uxplay.exe", StringComparison.OrdinalIgnoreCase)
            || EngineSha256 == null || EngineSha256.Length != 64 || !EngineSha256.All(Uri.IsHexDigit)) throw new InvalidDataException("请选择 UxPlayEnhanced 包中的 uxplay.exe。");
        if (checkFiles)
        {
            using var file = File.OpenRead(EnginePath);
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(EngineSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("AirPlay 引擎已改变，请重新选择引擎并应用。");
            if (!Directory.Exists(Path.Combine(Path.GetDirectoryName(EnginePath)!, "lib", "gstreamer-1.0"))) throw new IOException("缺少 GStreamer 插件目录，请保留完整的 UxPlayEnhanced 包。");
        }
    }
    public static AirPlaySettings Read(string root)
    {
        var path = Path.Combine(root, "airplay.json"); if (!File.Exists(path)) return Disabled;
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("AirPlay 配置过大。");
        var result = JsonSerializer.Deserialize<AirPlaySettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("AirPlay 配置为空。");
        result.Validate(); return result;
    }
}
public sealed record AirPlayReport(string Revision, string State, string Error, DateTime UpdatedUtc, int ProcessId,
    int Starts, int UnexpectedExits, double CpuPercent, double AverageCpuPercent, double PeakCpuPercent, long WorkingSetBytes, double UptimeSeconds, string[] Events);

// Optional third-party receiver is launched as a separate executable, never linked into ChannelBridge.
public sealed class AirPlayWorker : IDisposable
{
    readonly string root;
    readonly Func<AirPlaySettings, string, string, ProcessStartInfo> startInfo;
    readonly ConcurrentQueue<string> events = new();
    AirPlaySettings current = AirPlaySettings.Disabled;
    Process? process;
    EventWaitHandle? stop;
    ReceiverJob? job;
    AirPlayDiscovery? discovery;
    readonly bool nativeDiscovery;
    DateTime retry, started;
    string error = "";
    string outputSource = "";
    int starts, exits, consecutiveFailures;
    TimeSpan previousCpu;
    double previousSeconds, peak;
    public AirPlayWorker(string root, Func<AirPlaySettings, string, string, ProcessStartInfo>? startInfo = null)
    { this.root = root; this.startInfo = startInfo ?? StartInfo; nativeDiscovery = startInfo == null; }
    void Log(string text)
    {
        events.Enqueue($"{DateTime.UtcNow:O} {text[..Math.Min(text.Length, 512)]}");
        while (events.Count > 64) events.TryDequeue(out _);
    }
    public static ProcessStartInfo StartInfo(AirPlaySettings settings, string stopName, string root)
    {
        string folder = Path.GetDirectoryName(settings.EnginePath)!;
        var info = new ProcessStartInfo(settings.EnginePath) { WorkingDirectory = folder, UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-n", settings.ReceiverName, "-nh", "-vs", "0", "-no-progress",
            "-m", DeviceId(root), "-key", Path.Combine(root, "airplay-receiver.pem") }) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("-as");
        info.ArgumentList.Add(AudioSink(SourceId(root)));
        info.Environment["GST_PLUGIN_PATH"] = Path.Combine(folder, "lib", "gstreamer-1.0");
        info.Environment["GST_PLUGIN_SYSTEM_PATH"] = "";
        info.Environment["GST_REGISTRY"] = Path.Combine(root, "airplay-gstreamer-registry.bin");
        info.Environment["GST_DEBUG"] = "2";
        info.Environment["GST_DEBUG_NO_COLOR"] = "1";
        info.Environment["PATH"] = folder + Path.PathSeparator + info.Environment["PATH"];
        info.Environment["UXPLAYENHANCED_STOP_EVENT"] = stopName;
        info.Environment["UXPLAYENHANCED_PARENT_PID"] = Environment.ProcessId.ToString();
        return info;
    }
    public static string SourceId(string root)
    {
        string path = Path.Combine(root, "config.json");
        return File.Exists(path) ? ServiceFiles.Read(path).Profile.SourceId ?? "" : "";
    }
    public static string AudioSink(string sourceId) => sourceId.Length == 0 ? "wasapisink" :
        "wasapisink device=\"" + sourceId.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    public static string DeviceId(string root)
    {
        // Independent of the physical NIC and another UxPlay instance, stable across service restarts.
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("ChannelBridge.AirPlay/v1/" + Environment.MachineName.ToUpperInvariant() + "/" + Path.GetFullPath(root).ToUpperInvariant()));
        hash[0] = (byte)((hash[0] | 2) & 0xfe); // Locally administered, unicast identifier.
        return string.Join(":", hash.Take(6).Select(b => b.ToString("x2")));
    }
    public void Tick()
    {
        try
        {
            var next = AirPlaySettings.Read(root);
            string nextSource = next.Enabled ? SourceId(root) : "";
            if (next != current || nextSource != outputSource)
            { Stop(); current = next; outputSource = nextSource; retry = DateTime.MinValue; consecutiveFailures = 0; error = ""; }
            if (!current.Enabled) { Stop(); error = ""; }
            else
            {
                if (process?.HasExited == true)
                { error = "AirPlay 引擎意外退出：" + process.ExitCode; exits++; consecutiveFailures++; Log(error); Stop(); retry = DateTime.UtcNow.AddSeconds(30); }
                if (process == null && DateTime.UtcNow >= retry && consecutiveFailures < 5)
                {
                    retry = DateTime.UtcNow.AddSeconds(30);
                    current.Validate(true);
                    string stopName = "Local\\ChannelBridge-AirPlay-" + Guid.NewGuid().ToString("N");
                    stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
                    job = new ReceiverJob();
                    process = new Process { StartInfo = startInfo(current, stopName, root) };
                    process.OutputDataReceived += Output; process.ErrorDataReceived += Output;
                    if (!process.Start()) throw new IOException("无法启动 AirPlay 引擎。");
                    job.Assign(process); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    starts++; started = DateTime.UtcNow; previousCpu = TimeSpan.Zero; previousSeconds = peak = 0; error = "";
                    Log("AirPlay receiver process started (playback not verified)");
                    if (nativeDiscovery) discovery = new AirPlayDiscovery(process.Id, current.ReceiverName, DeviceId(root), Log);
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message; Log(error); Stop(); consecutiveFailures++;
            if (ex is InvalidDataException or JsonException) current = AirPlaySettings.Disabled;
        }
        double cpu = 0, average = 0, uptime = 0; long memory = 0; int pid = 0;
        try
        {
            if (process != null && !process.HasExited)
            {
                process.Refresh(); pid = process.Id; uptime = (DateTime.UtcNow - started).TotalSeconds;
                if (uptime > 60) consecutiveFailures = 0;
                var totalCpu = process.TotalProcessorTime;
                cpu = Math.Clamp((totalCpu - previousCpu).TotalSeconds / Math.Max(.001, uptime - previousSeconds) / Environment.ProcessorCount * 100, 0, 100);
                average = Math.Clamp(totalCpu.TotalSeconds / Math.Max(.001, uptime) / Environment.ProcessorCount * 100, 0, 100);
                peak = Math.Max(peak, cpu); previousCpu = totalCpu; previousSeconds = uptime; memory = process.WorkingSet64;
            }
        }
        catch (InvalidOperationException) { }
        var report = new AirPlayReport(current.Revision, error.Length > 0 ? "Error" : !current.Enabled ? "Disabled" : pid != 0 ? "Running" : "Waiting",
            error, DateTime.UtcNow, pid, starts, exits, cpu, average, peak, memory, uptime, events.ToArray());
        ServiceFiles.AtomicWrite(Path.Combine(root, "airplay-status.json"), report);
    }
    void Output(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is { Length: > 0 } text && ShouldLog(text)) Log(text);
    }
    public static bool ShouldLog(string text) => text.StartsWith("ct=", StringComparison.Ordinal)
        || text.StartsWith("audio quality:", StringComparison.OrdinalIgnoreCase)
        || new[] { "error", "fail", "warn", "connect", "listening", "started", "stopped", "mdns" }.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
    void Stop()
    {
        discovery?.Dispose(); discovery = null;
        var old = process; process = null;
        try
        {
            stop?.Set();
            if (old != null)
            {
                try { if (!old.HasExited && !old.WaitForExit(5000)) old.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Log(ex.Message); }
                old.OutputDataReceived -= Output; old.ErrorDataReceived -= Output; old.Dispose();
            }
        }
        finally { job?.Dispose(); job = null; stop?.Dispose(); stop = null; }
    }
    public void Dispose() => Stop();
    public static async Task Run(string root, CancellationToken token)
    {
        using var worker = new AirPlayWorker(root);
        while (!token.IsCancellationRequested)
        {
            try { worker.Tick(); } catch (Exception ex) { Trace.WriteLine(ex); }
            try { await Task.Delay(2000, token); } catch (OperationCanceledException) { break; }
        }
    }
}
