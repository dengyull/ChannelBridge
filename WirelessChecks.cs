using System.Collections.Concurrent;
using System.Text.Json;

namespace ChannelBridge;
public static class WirelessChecks
{
    public static async Task<string> Run(string directory)
    {
        void Check(bool value, string label) { if (!value) throw new Exception(label); }
        async Task Until(Func<bool> condition)
        {
            for (int i = 0; i < 400; i++) { if (condition()) return; await Task.Delay(5).ConfigureAwait(false); }
            throw new Exception("Wireless state transition timed out");
        }
        string root = Path.Combine(directory, "wireless-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        void Save(bool enabled, params string[] ids) => ServiceFiles.AtomicWrite(Path.Combine(root, "wireless.json"), new WirelessSettings(enabled, ids, Guid.NewGuid().ToString("N")));
        var backend = new FakeBackend(); int created = 0;
        using (var worker = new WirelessWorker(root, () => { created++; return backend; }, TimeSpan.FromMilliseconds(10)))
        {
            worker.Tick(); Check(created == 0 && worker.Snapshot().State == "Disabled", "Disabled must not initialize WinRT or discovery");
            Save(true, "phone"); worker.Tick(); await Until(() => worker.Snapshot().Devices.Single().State == "Connected");
            Check(created == 1 && backend.Attempts == 1, "Enabled connects selected device");
            worker.Tick(); Check(backend.Attempts == 1, "Ticks must not recreate stable connection");
            backend.Last!.Dispose(); await Until(() => backend.Attempts == 2 && worker.Snapshot().Devices.Single().State == "Connected");
            Check(worker.Snapshot().Devices.Single().Disconnects == 1, "Unexpected disconnect recorded and retried");
            Save(true); worker.Tick(); await Until(() => backend.Last!.Disposed);
            Check(worker.Snapshot().Devices.Single().Selected == false, "Disconnect cancels saved reconnect intent");
            backend.Deny = true; Save(true, "phone"); worker.Tick(); await Until(() => worker.Snapshot().Devices.Single().State == "Denied");
            int deniedAttempts = backend.Attempts; await Task.Delay(50); worker.Tick(); Check(backend.Attempts == deniedAttempts, "System denial does not hot-loop");
            backend.Deny = false; Save(true, "phone"); worker.Tick(); await Until(() => worker.Snapshot().Devices.Single().State == "Connected");
            Save(false, "phone"); worker.Tick(); await Until(() => backend.Last!.Disposed);
            Check(backend.Disposed && worker.Snapshot().State == "Disabled", "Disable releases connection and device watcher");
        }
        Save(true, "phone"); var restarted = new FakeBackend();
        using (var worker = new WirelessWorker(root, () => restarted, TimeSpan.FromMilliseconds(10)))
        {
            worker.Tick(); await Until(() => worker.Snapshot().Devices.Single().State == "Connected");
            File.WriteAllText(Path.Combine(root, "wireless.json"), "{"); worker.Tick();
            await Until(() => restarted.Last!.Disposed);
            Check(worker.Snapshot().State == "Error" && restarted.Disposed, "Corrupt config fails closed");
        }
        var late = new FakeBackend { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) }; Save(true, "phone");
        using (var worker = new WirelessWorker(root, () => late, TimeSpan.FromMilliseconds(10)))
        {
            worker.Tick(); await Until(() => late.Attempts == 1);
            Save(false); worker.Tick();
            var delayed = new FakeConnection(); late.Pending.SetResult(delayed);
            await Until(() => delayed.Disposed); Check(worker.Snapshot().State == "Disabled", "Late connection cannot resurrect disabled receiving");
        }
        var offline = new FakeBackend { Offline = true }; Save(true, "phone");
        using (var worker = new WirelessWorker(root, () => offline, TimeSpan.FromMilliseconds(10)))
        {
            worker.Tick(); await Until(() => worker.Snapshot().Devices.Length == 1);
            await Task.Delay(30); Check(offline.Attempts == 0, "Offline device does not trigger repeated connect calls");
            offline.Offline = false; await Until(() => worker.Snapshot().Devices.Single().State == "Connected");
            var report = worker.Snapshot(); Check(double.IsFinite(report.CpuPercent) && report.CpuPercent is >= 0 and <= 100, "Normalized CPU metric");
        }
        return "PASS: wireless default-off, persistent intent, reconnect/disconnect, denied backoff, corrupt config, shutdown/late completion and offline recovery\nPASS: bounded diagnostics and finite process CPU metrics\nNOTE: simulated Bluetooth backend; no phone connection or playback tested.\n";
    }
    sealed class FakeConnection : IWirelessConnection
    {
        readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed => closed.Task.IsCompleted;
        public Task Closed => closed.Task;
        public void Dispose() => closed.TrySetResult();
    }
    sealed class FakeBackend : IWirelessBackend
    {
        public volatile bool Disposed, Deny, Offline;
        public int Attempts;
        public FakeConnection? Last;
        public TaskCompletionSource<IWirelessConnection>? Pending;
        public string Error => "";
        public IReadOnlyList<WirelessDevice> Devices => Offline ? Array.Empty<WirelessDevice>() : new[] { new WirelessDevice("phone", "Demo phone") };
        public Task<IWirelessConnection> ConnectAsync(string id, CancellationToken token)
        {
            Interlocked.Increment(ref Attempts);
            if (Deny) throw new WirelessDeniedException("Simulated system denial");
            if (Pending != null) return Pending.Task;
            Last = new FakeConnection(); return Task.FromResult<IWirelessConnection>(Last);
        }
        public void Dispose() => Disposed = true;
    }
}
