using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ChannelBridge;
public sealed record ApplyRequest(SurroundProfile Profile, bool? Enabled);
public static class ProfileApplication
{
    public static void Validate(SurroundProfile profile)
    {
        SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(profile), Array.Empty<Endpoint>());
        var layout = SpeakerLayouts.Get(profile.LayoutId);
        var devices = AudioEngine.Devices();
        var source = devices.FirstOrDefault(d => d.Id == profile.SourceId) ?? throw new IOException("音源设备未连接。");
        var expected = source with { Channels = layout.Roles.Length, Mask = WindowsSpeakerMode.Mask(layout) };
        SpeakerLayouts.BuildRoutes(SpeakerLayouts.Resolve(profile, expected), expected, devices);
    }
    public static void ApplyCore(ApplyRequest request)
    {
        Validate(request.Profile);
        using var change = new WindowsSpeakerMode(request.Profile.SourceId, SpeakerLayouts.Get(request.Profile.LayoutId));
        var devices = AudioEngine.Devices();
        var profile = request.Profile;
        profile.Speakers = SpeakerLayouts.Resolve(profile, change.Actual);
        SpeakerLayouts.BuildRoutes(profile.Speakers, change.Actual, devices);
        bool enabled = request.Enabled ?? ServiceFiles.ReadSaved()?.Enabled ?? false;
        ServiceFiles.AtomicWrite(ServiceFiles.ConfigPath, new ServiceConfig(enabled, profile, Guid.NewGuid().ToString("N")));
        change.Commit();
    }
    static bool AccessDenied(Exception ex) => ex.HResult == unchecked((int)0x80070005) || ex.InnerException != null && AccessDenied(ex.InnerException);
    public static void Apply(ApplyRequest request)
    {
        Validate(request.Profile);
        ServiceSetup.EnsureStarted();
        try { ApplyCore(request); }
        catch (Exception ex) when (AccessDenied(ex))
        {
            string token = Guid.NewGuid().ToString("N"), result = ResultPath(token);
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add("--apply-windows-profile");
            psi.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
            psi.ArgumentList.Add(token);
            using var helper = Process.Start(psi)!; helper.WaitForExit();
            try
            {
                if (!File.Exists(result)) throw new IOException("管理员设置程序未返回结果，配置未确认应用。");
                var error = JsonSerializer.Deserialize<string>(File.ReadAllText(result));
                if (helper.ExitCode != 0 || error != "") throw new IOException(error ?? "Windows 声道设置失败。");
            }
            finally { if (File.Exists(result)) File.Delete(result); }
        }
    }
    public static string ResultPath(string token)
    {
        if (!Guid.TryParseExact(token, "N", out _)) throw new ArgumentException("Invalid result token");
        return Path.Combine(ServiceFiles.Root, "apply-" + token + ".json");
    }
}
