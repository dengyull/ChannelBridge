using System.Collections.Concurrent;

namespace ChannelBridge;

// Runs separately from the audio worker: Bluetooth discovery/connection cannot stall WASAPI forwarding.
public sealed class WirelessWorker : IDisposable
{
    readonly string root;
    readonly Func<IWirelessBackend> factory;
    readonly TimeSpan retryUnit;
    readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    readonly ConcurrentQueue<string> events = new();
    readonly WirelessMetrics metrics = new();
    IWirelessBackend? backend;
    WirelessSettings current = WirelessSettings.Disabled;
    string error = "";
    DateTime nextBackendAttempt;
    bool disposed;
    sealed class Entry(string id)
    {
        public readonly CancellationTokenSource Cancellation = new();
        public Task Task = Task.CompletedTask;
        public WirelessDeviceStatus Status = new(id, "Unavailable device", false, true, "Waiting", 0, 0, 0, "", null);
    }
    public WirelessWorker(string root, Func<IWirelessBackend>? factory = null, TimeSpan? retryUnit = null)
    { this.root = root; this.factory = factory ?? (() => new A2dpBackend()); this.retryUnit = retryUnit ?? TimeSpan.FromSeconds(5); }
    void Log(string message)
    {
        events.Enqueue($"{DateTime.UtcNow:O} {message}");
        while (events.Count > 64) events.TryDequeue(out _);
    }
    public void Tick()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            var next = WirelessSettings.Read(root);
            bool changed = next.Revision != current.Revision;
            if (changed) { nextBackendAttempt = DateTime.MinValue; Log(next.A2dpEnabled ? "A2DP enabled/configuration updated" : "A2DP disabled"); }
            current = next; error = "";
            if (!current.A2dpEnabled) StopBackend();
            else
            {
                if (backend?.Error is { Length: > 0 } backendError)
                { StopBackend(); nextBackendAttempt = DateTime.UtcNow.AddSeconds(30); throw new IOException(backendError); }
                if (backend == null && DateTime.UtcNow >= nextBackendAttempt)
                {
                    nextBackendAttempt = DateTime.UtcNow.AddSeconds(30);
                    backend = factory(); Log("A2DP device watcher started");
                }
                foreach (var id in entries.Keys.ToArray())
                    if (!current.DeviceIds.Contains(id, StringComparer.Ordinal) || changed && entries[id].Status.State == "Denied") StopEntry(id);
                if (backend != null)
                {
                    foreach (var id in current.DeviceIds)
                    {
                        if (entries.ContainsKey(id)) continue;
                        var entry = new Entry(id); entries.Add(id, entry);
                        var activeBackend = backend;
                        entry.Task = Task.Run(() => ConnectLoop(activeBackend, entry));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            // Invalid/missing authorization must not leave previously enabled receiving active.
            if (ex is InvalidDataException or System.Text.Json.JsonException) { StopBackend(); current = WirelessSettings.Disabled; }
            Log(error);
        }
        WriteReport();
    }
    async Task ConnectLoop(IWirelessBackend activeBackend, Entry entry)
    {
        var token = entry.Cancellation.Token;
        int consecutive = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var device = activeBackend.Devices.FirstOrDefault(d => d.Id == entry.Status.Id);
                if (device == null)
                {
                    entry.Status = entry.Status with { Available = false, State = "Waiting", ConnectedUtc = null };
                    await Task.Delay(retryUnit, token); continue;
                }
                entry.Status = entry.Status with { Name = device.Name, Available = true, State = "Connecting", Attempts = entry.Status.Attempts + 1, Error = "" };
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    using var connection = await activeBackend.ConnectAsync(device.Id, timeout.Token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    consecutive = 0;
                    entry.Status = entry.Status with { State = "Connected", ConnectedUtc = DateTime.UtcNow };
                    Log("A2DP connected: " + device.Name);
                    await connection.Closed.WaitAsync(token).ConfigureAwait(false);
                    entry.Status = entry.Status with { State = "Retrying", ConnectedUtc = null, Disconnects = entry.Status.Disconnects + 1 };
                    Log("A2DP disconnected: " + device.Name);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (WirelessDeniedException ex)
                {
                    entry.Status = entry.Status with { State = "Denied", Error = ex.Message, Failures = entry.Status.Failures + 1, ConnectedUtc = null };
                    Log(ex.Message); await Task.Delay(Timeout.Infinite, token); break;
                }
                catch (Exception ex)
                {
                    entry.Status = entry.Status with { State = "Retrying", Error = ex is OperationCanceledException ? "A2DP 连接超时。" : ex.Message,
                        Failures = entry.Status.Failures + 1, ConnectedUtc = null };
                    Log("A2DP connection failed: " + entry.Status.Error);
                }
                consecutive = Math.Min(consecutive + 1, 6);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(120000, retryUnit.TotalMilliseconds * Math.Pow(2, consecutive - 1))), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { entry.Status = entry.Status with { State = "Denied", Error = ex.Message, Failures = entry.Status.Failures + 1 }; Log(ex.Message); }
    }
    void StopEntry(string id)
    {
        var entry = entries[id]; entries.Remove(id);
        try { entry.Cancellation.Cancel(); } catch (Exception ex) { Log(ex.Message); }
        _ = entry.Task.ContinueWith(_ => entry.Cancellation.Dispose(), TaskScheduler.Default);
    }
    void StopBackend()
    {
        foreach (var id in entries.Keys.ToArray()) StopEntry(id);
        var old = backend; backend = null;
        try { old?.Dispose(); } catch (Exception ex) { Log(ex.Message); }
    }
    public WirelessReport Snapshot()
    {
        var discovered = backend?.Devices ?? Array.Empty<WirelessDevice>();
        var all = discovered.Select(d => new WirelessDeviceStatus(d.Id, d.Name, true, false, "Available", 0, 0, 0, "", null)).ToDictionary(d => d.Id);
        foreach (var entry in entries.Values) all[entry.Status.Id] = entry.Status;
        var m = metrics.Sample();
        return new(current.Revision, current.A2dpEnabled, error.Length > 0 ? "Error" : !current.A2dpEnabled ? "Disabled" : backend == null ? "Waiting" : "Ready",
            error, DateTime.UtcNow, Environment.ProcessId, m.Cpu, m.Average, m.Peak, m.Memory, m.Handles, m.Uptime,
            all.Values.OrderBy(d => d.Name).ToArray(), events.ToArray());
    }
    void WriteReport() => ServiceFiles.AtomicWrite(Path.Combine(root, "wireless-status.json"), Snapshot());
    public void Dispose() { if (disposed) return; disposed = true; StopBackend(); metrics.Dispose(); }
    public static async Task Run(string root, CancellationToken token)
    {
        using var worker = new WirelessWorker(root);
        while (!token.IsCancellationRequested)
        {
            try { worker.Tick(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
            try { await Task.Delay(2000, token); } catch (OperationCanceledException) { break; }
        }
    }
}
