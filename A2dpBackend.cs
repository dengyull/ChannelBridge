using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace ChannelBridge;

// Uses the same public Windows API as ysc3839/AudioPlaybackConnector (MIT).
// No Bluetooth codec or audio forwarding loop is implemented by this connector.
public sealed class A2dpBackend : IWirelessBackend
{
    readonly object gate = new();
    readonly Dictionary<string, WirelessDevice> devices = new(StringComparer.Ordinal);
    readonly DeviceWatcher watcher;
    string error = "";
    bool disposed;
    public IReadOnlyList<WirelessDevice> Devices { get { lock (gate) return devices.Values.OrderBy(d => d.Name).ToArray(); } }
    public string Error { get { lock (gate) return error; } }
    public A2dpBackend()
    {
        watcher = DeviceInformation.CreateWatcher(AudioPlaybackConnection.GetDeviceSelector());
        watcher.Added += Added; watcher.Updated += Updated; watcher.Removed += Removed; watcher.Stopped += Stopped;
        try { watcher.Start(); } catch { Dispose(); throw; }
    }
    void Added(DeviceWatcher _, DeviceInformation info) { lock (gate) if (!disposed) devices[info.Id] = new(info.Id, info.Name); }
    void Updated(DeviceWatcher _, DeviceInformationUpdate info)
    {
        lock (gate) if (!disposed && devices.TryGetValue(info.Id, out var old)
            && info.Properties.TryGetValue("System.ItemNameDisplay", out var name) && name is string text) devices[info.Id] = old with { Name = text };
    }
    void Removed(DeviceWatcher _, DeviceInformationUpdate info) { lock (gate) devices.Remove(info.Id); }
    void Stopped(DeviceWatcher _, object args) { lock (gate) if (!disposed) error = "蓝牙设备监视器已停止。"; }
    public async Task<IWirelessConnection> ConnectAsync(string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        AudioPlaybackConnection connection;
        try { connection = AudioPlaybackConnection.TryCreateFromId(id) ?? throw new IOException("无法创建设备的 A2DP 连接。"); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070005))
        { throw new WirelessDeniedException("Windows 拒绝 A2DP 连接；服务会话或系统策略可能不支持此操作。"); }
        var holder = new Connection(connection);
        using var cancellation = token.Register(holder.Dispose);
        try
        {
            await connection.StartAsync().AsTask(token).ConfigureAwait(false);
            var result = await connection.OpenAsync().AsTask(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.Status == AudioPlaybackConnectionOpenResultStatus.DeniedBySystem)
                throw new WirelessDeniedException("Windows 拒绝 A2DP 连接；服务会话或系统策略可能不支持此操作。");
            if (result.Status != AudioPlaybackConnectionOpenResultStatus.Success)
                throw new IOException($"A2DP: {result.Status} (0x{result.ExtendedError?.HResult ?? 0:X8})");
            holder.Arm(); return holder;
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070005))
        { holder.Dispose(); throw new WirelessDeniedException("Windows 拒绝 A2DP 连接；服务会话或系统策略可能不支持此操作。"); }
        catch { holder.Dispose(); throw; }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; devices.Clear(); }
        watcher.Added -= Added; watcher.Updated -= Updated; watcher.Removed -= Removed; watcher.Stopped -= Stopped;
        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
    }
    sealed class Connection : IWirelessConnection
    {
        readonly AudioPlaybackConnection connection;
        readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int disposed;
        volatile bool armed;
        public Task Closed => closed.Task;
        public Connection(AudioPlaybackConnection connection) { this.connection = connection; connection.StateChanged += Changed; }
        public void Arm() { armed = true; CheckState(); }
        void Changed(AudioPlaybackConnection sender, object args) => CheckState();
        public void CheckState()
        {
            if (!armed) return;
            try { if (connection.State == AudioPlaybackConnectionState.Closed) closed.TrySetResult(); }
            catch (Exception) { closed.TrySetResult(); }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { connection.StateChanged -= Changed; connection.Dispose(); }
            finally { closed.TrySetResult(); }
        }
    }
}
