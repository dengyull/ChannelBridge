using System.Diagnostics;
using System.Text.Json;

namespace ChannelBridge;

public sealed record WirelessSettings(bool A2dpEnabled, string[] DeviceIds, string Revision)
{
    public static WirelessSettings Disabled => new(false, Array.Empty<string>(), "default");
    public void Validate()
    {
        if (DeviceIds == null || DeviceIds.Length > 8 || DeviceIds.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 2048)
            || DeviceIds.Distinct(StringComparer.Ordinal).Count() != DeviceIds.Length || string.IsNullOrEmpty(Revision) || Revision.Length > 64)
            throw new InvalidDataException("无线接收配置无效。");
    }
    public static WirelessSettings Read(string root)
    {
        string path = Path.Combine(root, "wireless.json");
        if (!File.Exists(path)) return Disabled;
        if (new FileInfo(path).Length > 32768) throw new InvalidDataException("无线接收配置过大。");
        var value = JsonSerializer.Deserialize<WirelessSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("无线接收配置为空。");
        value.Validate(); return value;
    }
}
public sealed record WirelessDevice(string Id, string Name);
public sealed record WirelessDeviceStatus(string Id, string Name, bool Available, bool Selected, string State,
    int Attempts, int Failures, int Disconnects, string Error, DateTime? ConnectedUtc);
public sealed record WirelessReport(string Revision, bool Enabled, string State, string Error, DateTime UpdatedUtc,
    int ProcessId, double CpuPercent, double AverageCpuPercent, double PeakCpuPercent, long WorkingSetBytes,
    int Handles, double UptimeSeconds, WirelessDeviceStatus[] Devices, string[] Events);

public interface IWirelessConnection : IDisposable
{
    Task Closed { get; }
}
public interface IWirelessBackend : IDisposable
{
    IReadOnlyList<WirelessDevice> Devices { get; }
    string Error { get; }
    Task<IWirelessConnection> ConnectAsync(string id, CancellationToken token);
}
public sealed class WirelessDeniedException(string message) : IOException(message);

// CPU is the whole service process, normalized to the machine's logical processor count.
// Bluetooth decoding performed inside Windows audio processes is not included.
public sealed class WirelessMetrics : IDisposable
{
    readonly Process process = Process.GetCurrentProcess();
    readonly Stopwatch clock = Stopwatch.StartNew();
    TimeSpan previousCpu;
    double previousTime, sumCpu, peak;
    public (double Cpu, double Average, double Peak, long Memory, int Handles, double Uptime) Sample()
    {
        process.Refresh(); double now = clock.Elapsed.TotalSeconds;
        var cpu = process.TotalProcessorTime;
        double elapsed = now - previousTime;
        double delta = previousTime == 0 ? 0 : Math.Max(0, (cpu - previousCpu).TotalSeconds);
        double value = elapsed <= 0 ? 0 : Math.Clamp(delta / elapsed / Environment.ProcessorCount * 100, 0, 100);
        sumCpu += delta; peak = Math.Max(peak, value); previousCpu = cpu; previousTime = now;
        return (value, now == 0 ? 0 : sumCpu / now / Environment.ProcessorCount * 100, peak, process.WorkingSet64, process.HandleCount, now);
    }
    public void Dispose() => process.Dispose();
}
