using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;

namespace ChannelBridge;

public sealed record ServiceConfig(bool Enabled, SurroundProfile Profile, string Revision);
public sealed record ServiceReport(string State, string Message, string Revision, long Frames, DateTime UpdatedUtc, int ProcessId, string TestRole = "", int TestIndex = 0, int TestCount = 0, string Version = "1.1.0", float SourceGain = 1, int SourceChannels = 0, int BufferMs = 80, long Underruns = 0, long Overruns = 0);

public static class ServiceFiles
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ChannelBridge");
    public static string ConfigPath => Path.Combine(Root, "config.json");
    public static void AtomicWrite<T>(string path, T value)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static ServiceConfig Read(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("配置过大。");
        var c = JsonSerializer.Deserialize<ServiceConfig>(File.ReadAllText(path)) ?? throw new InvalidDataException("配置为空。");
        if (c.Profile == null || string.IsNullOrEmpty(c.Revision)) throw new InvalidDataException("配置不完整。");
        c = c with { Profile = SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(c.Profile), Array.Empty<Endpoint>()) };
        return c;
    }
    public static ServiceConfig? ReadSaved()
    {
        try { return Read(ConfigPath); } catch { try { return Read(ConfigPath + ".bak"); } catch { return null; } }
    }
}

// A single worker owns all WASAPI resources. Stopping the host never changes the saved Enabled intent.
public sealed class RoutingWorker : IDisposable
{
    readonly string root;
    readonly bool simulate;
    readonly Action<string> applyPriority;
    AudioEngine? engine;
    ServiceConfig? current;
    string? fault;
    bool running;
    DateTime retry;
    AudioTestSequence? testSequence;
    string lastTestId = "", testError = "";
    ServiceReport? lastReport;
    DateTime nextFormatCheck;
    (int Channels, int Mask, int Rate) sourceFormat;
    CalibrationRun? calibration;
    string lastCalibrationId = "";
    public RoutingWorker(string root, bool simulate = false, Action<string>? applyPriority = null)
    {
        this.root = root; this.simulate = simulate;
        this.applyPriority = applyPriority ?? (simulate ? _ => { } : ServicePriority.Apply);
        try { lastTestId = JsonSerializer.Deserialize<TestRequest>(File.ReadAllText(Path.Combine(root, "test.json")))?.Id ?? ""; } catch { }
        try { lastCalibrationId = JsonSerializer.Deserialize<CalibrationCommand>(File.ReadAllText(Path.Combine(root, "calibration-request.json")))?.Id ?? ""; } catch { }
    }
    public void Tick()
    {
        string path = Path.Combine(root, "config.json");
        string message = "路由已停止";
        try
        {
            ServiceConfig? next = null;
            if (File.Exists(path))
            {
                try { next = ServiceFiles.Read(path); }
                catch { if (current == null && File.Exists(path + ".bak")) next = ServiceFiles.Read(path + ".bak"); else throw; }
            }
            if (next != null && next.Revision != current?.Revision)
            {
                applyPriority(next.Profile.ServicePriority);
                StopCalibration(); DisposeTest(); DisposeAudio(); current = next; retry = DateTime.MinValue;
            }
            var error = Interlocked.Exchange(ref fault, null);
            if (error != null) { DisposeAudio(); retry = DateTime.UtcNow.AddSeconds(3); message = error; }
            HandleCalibrationCommand();
            if (calibration?.Completed == true) StopCalibration();
            HandleTestCommand();
            if (testSequence != null)
            {
                try { if (!testSequence.Tick(DateTime.UtcNow)) DisposeTest(); }
                catch (Exception ex) { DisposeTest(); testError = "测试失败：" + ex.Message; }
            }
            if (current?.Enabled == true && testSequence == null && calibration == null)
            {
                if (running && !simulate)
                {
                    try
                    {
                        engine!.UpdateSourceVolume();
                        if (DateTime.UtcNow >= nextFormatCheck)
                        {
                            nextFormatCheck = DateTime.UtcNow.AddSeconds(1);
                            var actual = AudioEngine.DeviceFormat(current.Profile.SourceId);
                            if (sourceFormat != actual || !engine.OutputFormatsMatch())
                            { DisposeAudio(); retry = DateTime.MinValue; }
                        }
                    }
                    catch (Exception ex) { DisposeAudio(); message = ex.Message; retry = DateTime.UtcNow.AddSeconds(1); }
                }
                if (!running && DateTime.UtcNow >= retry)
                {
                    try
                    {
                        if (simulate) { if (File.Exists(Path.Combine(root, "device-missing"))) throw new IOException("模拟设备离线"); }
                        else
                        {
                            var devices = AudioEngine.Devices();
                            var source = devices.FirstOrDefault(d => d.Id == current.Profile.SourceId) ?? throw new IOException("等待原音源设备连接");
                            var routes = SpeakerLayouts.BuildRoutes(SpeakerLayouts.Resolve(current.Profile, source), source, devices);
                            sourceFormat = (source.Channels, source.Mask, source.Rate);
                            engine = new AudioEngine(); engine.Fault += e => Interlocked.Exchange(ref fault, e);
                            engine.Start(source.Id, routes, current.Profile.FollowSourceVolume, current.Profile.Buffers.QueueMs, current.Profile.Buffers.CaptureMs, current.Profile.Buffers.OutputMs);
                        }
                        running = true;
                    }
                    catch (Exception ex) { DisposeAudio(); message = ex.Message; retry = DateTime.UtcNow.AddSeconds(3); }
                }
                if (running) message = "后台路由运行中；关闭界面不影响播放";
                else if (message == "路由已停止") message = "等待设备 / 音频服务恢复，自动重试中";
            }
        }
        catch (Exception ex) { message = "配置读取失败，保留当前运行配置：" + ex.Message; }
        var report = new ServiceReport(calibration != null ? "Calibrating" : testSequence != null ? "Testing" : running ? "Running" : current?.Enabled == true ? "Waiting" : "Stopped", calibration != null ? calibration.Progress : testSequence != null ? $"正在测试 {testSequence.Index}/{testSequence.Count} · {testSequence.Role} · {SpeakerLayouts.Role(testSequence.Role).Name}" : testError.Length > 0 ? testError : message, current?.Revision ?? "", engine?.FramesCaptured ?? 0, DateTime.UtcNow, Environment.ProcessId, testSequence?.Role ?? "", testSequence?.Index ?? 0, testSequence?.Count ?? 0, SourceGain: engine?.SourceGain ?? 1, SourceChannels: engine?.SourceChannels ?? 0, BufferMs: current?.Profile.BufferMs ?? 80, Underruns: engine?.BufferUnderruns ?? 0, Overruns: engine?.BufferOverruns ?? 0);
        if (lastReport == null || report.State != lastReport.State || report.Message != lastReport.Message || report.Revision != lastReport.Revision || report.UpdatedUtc - lastReport.UpdatedUtc >= TimeSpan.FromSeconds(1))
        { ServiceFiles.AtomicWrite(Path.Combine(root, "status.json"), report); lastReport = report; }
    }
    void HandleTestCommand()
    {
        string path = Path.Combine(root, "test.json");
        if (!File.Exists(path)) return;
        try
        {
            if (new FileInfo(path).Length > 65536) throw new IOException("测试请求过大。");
            var request = JsonSerializer.Deserialize<TestRequest>(File.ReadAllText(path));
            if (request == null || request.Id == lastTestId) return;
            lastTestId = request.Id;
            if (request.ExpiresUtc < DateTime.UtcNow || request.ExpiresUtc > DateTime.UtcNow.AddMinutes(2)) return;
            if (calibration != null) throw new IOException("麦克风测量进行中，请先停止测量。");
            testError = "";
            if (request.Cancel) { DisposeTest(); return; }
            var next = new AudioTestSequence(request, simulate);
            DisposeTest(); DisposeAudio(); testSequence = next;
        }
        catch (Exception ex) { testError = "测试失败：" + ex.Message; }
    }
    void DisposeTest() { var old = testSequence; testSequence = null; old?.Dispose(); }
    void StopCalibration() { var old = calibration; calibration = null; old?.Dispose(); }
    void HandleCalibrationCommand()
    {
        string path = Path.Combine(root, "calibration-request.json");
        if (!File.Exists(path)) return;
        CalibrationCommand? command = null;
        try
        {
            if (new FileInfo(path).Length > 65536) throw new IOException("测量请求过大。");
            command = JsonSerializer.Deserialize<CalibrationCommand>(File.ReadAllText(path));
            if (command == null || command.Id == lastCalibrationId) return;
            lastCalibrationId = command.Id;
            if (command.ExpiresUtc < DateTime.UtcNow || command.ExpiresUtc > DateTime.UtcNow.AddMinutes(2)) return;
            if (command.Cancel) { if (calibration?.Id == command.RunId) StopCalibration(); return; }
            if (simulate) throw new IOException("模拟服务不采集麦克风。");
            if (calibration != null) throw new IOException("另一次测量正在进行。");
            if (current == null || command.Revision != current.Revision) throw new IOException("配置已变化，请重新开始测量。");
            if (command.Profile == null) throw new IOException("缺少测量配置。");
            DisposeTest(); DisposeAudio(); testError = "";
            calibration = new CalibrationRun(root, command);
        }
        catch (Exception ex)
        {
            if (command != null) ServiceFiles.AtomicWrite(Path.Combine(root, "calibration-result.json"), new CalibrationReport(command.RunId, false, false, ex.Message, new()));
            else testError = ex.Message;
        }
    }
    void DisposeAudio() { var old = engine; engine = null; old?.Dispose(); running = false; }
    public void Dispose() { StopCalibration(); DisposeTest(); DisposeAudio(); }
    public static async Task Run(string root, bool simulate, CancellationToken token, RoutingWorker? existing = null)
    {
        using var worker = existing ?? new RoutingWorker(root, simulate);
        while (!token.IsCancellationRequested) { worker.Tick(); try { await Task.Delay(200, token); } catch (OperationCanceledException) { break; } }
    }
}

public sealed class AudioWindowsService : ServiceBase
{
    public const string Id = "ChannelBridge.Audio";
    CancellationTokenSource? cancellation;
    Task? worker;
    public AudioWindowsService() { ServiceName = Id; AutoLog = true; CanShutdown = true; }
    protected override void OnStart(string[] args)
    {
        cancellation = new();
        // Consume stale test requests before SCM reports Running, so a fresh UI request cannot be lost.
        var routing = new RoutingWorker(ServiceFiles.Root);
        worker = Task.Run(() => RoutingWorker.Run(ServiceFiles.Root, false, cancellation.Token, routing));
        _ = worker.ContinueWith(t => Environment.Exit(1), TaskContinuationOptions.OnlyOnFaulted);
    }
    protected override void OnStop() { cancellation?.Cancel(); if (worker != null && !worker.Wait(TimeSpan.FromSeconds(20))) Environment.Exit(1); }
    protected override void OnShutdown() => OnStop();
}

public static class ServiceSetup
{
    public static bool Installed => ServiceController.GetServices().Any(s => s.ServiceName == AudioWindowsService.Id);
    static void Sc(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!; string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit();
        if (p.ExitCode != 0) throw new IOException("服务配置失败：" + output);
    }
    public static void Install(string owner)
    {
        var sid = new SecurityIdentifier(owner);
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ChannelBridge");
        Directory.CreateDirectory(folder);
        string executable = Path.Combine(folder, "ChannelBridge.exe");
        if (Installed)
        {
            using var existing = new ServiceController(AudioWindowsService.Id);
            if (existing.Status != ServiceControllerStatus.Stopped) { existing.Stop(); existing.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
        }
        if (!string.Equals(Environment.ProcessPath, executable, StringComparison.OrdinalIgnoreCase)) File.Copy(Environment.ProcessPath!, executable, true);
        Directory.CreateDirectory(ServiceFiles.Root);
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var id in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), sid })
            acl.AddAccessRule(new FileSystemAccessRule(id, FileSystemRights.Modify | FileSystemRights.Synchronize, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(ServiceFiles.Root).SetAccessControl(acl);
        Sc(Installed ? "config" : "create", AudioWindowsService.Id, "binPath=", "\"" + executable + "\" --service", "start=", "auto", "obj=", "NT AUTHORITY\\LocalService", "depend=", "AudioSrv/AudioEndpointBuilder", "DisplayName=", "ChannelBridge 音频路由");
        Sc("description", AudioWindowsService.Id, "将多声道虚拟播放设备分配到多个音频输出，重启后恢复保存的路由。");
        Sc("failure", AudioWindowsService.Id, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/60000");
        Sc("failureflag", AudioWindowsService.Id, "1");
        Sc("sdset", AudioWindowsService.Id, $"D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCRPWPLOCRRC;;;{sid.Value})");
        using var service = new ServiceController(AudioWindowsService.Id); service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }
    public static void EnsureStarted()
    {
        string installedExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ChannelBridge", "ChannelBridge.exe");
        bool outdated = !File.Exists(installedExe) || FileVersionInfo.GetVersionInfo(installedExe).FileVersion != FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).FileVersion;
        if (!Installed || outdated)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add("--install-service"); psi.ArgumentList.Add(WindowsIdentity.GetCurrent().User!.Value);
            using var install = Process.Start(psi)!; install.WaitForExit();
            if (install.ExitCode != 0) throw new IOException("服务安装未完成，请查看管理员窗口的错误提示。");
        }
        using var service = new ServiceController(AudioWindowsService.Id);
        if (service.Status != ServiceControllerStatus.Running) { service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }
    }
    public static void Restart()
    {
        EnsureStarted(); using var service = new ServiceController(AudioWindowsService.Id);
        service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }
}
