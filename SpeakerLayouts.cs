using System.Text.Json;

namespace ChannelBridge;

public record SpeakerRole(string Id, string Name, int Bit, float X, float Y);
public record SpeakerLayout(string Id, string Name, string[] Roles)
{
    public override string ToString() => $"{Name} · {Roles.Length} 声道";
}
public sealed class SpeakerSetting
{
    public string Role { get; set; } = "FL";
    public int SourceChannel { get; set; } = -1;
    public string DeviceId { get; set; } = "";
    public int Side { get; set; }
    public float GainDb { get; set; } = -2.5f;
    public int DelayMs { get; set; }
    public bool Muted { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public float LinearGain => Muted ? 0 : (float)Math.Pow(10, GainDb / 20);
}
public sealed class SurroundProfile
{
    public int Version { get; set; } = 2;
    public string SourceId { get; set; } = "";
    public string LayoutId { get; set; } = "5.1";
    public List<SpeakerSetting> Speakers { get; set; } = new();
    public bool AutoMatchSource { get; set; } = true;
    public bool FollowSourceVolume { get; set; } = true;
}
public static class SpeakerLayouts
{
    public static readonly SpeakerRole[] Roles = {
        new("FL", "前左", 1, .24f, .18f), new("FR", "前右", 2, .76f, .18f),
        new("FC", "中置", 4, .50f, .10f), new("LFE", "低音炮", 8, .82f, .40f),
        new("BL", "后左", 16, .25f, .84f), new("BR", "后右", 32, .75f, .84f),
        new("FLC", "前中左", 64, .36f, .14f), new("FRC", "前中右", 128, .64f, .14f),
        new("BC", "后中置", 256, .50f, .89f), new("SL", "侧左", 512, .14f, .62f), new("SR", "侧右", 1024, .86f, .62f)
    };
    public static readonly SpeakerLayout[] All = {
        new("2.0", "2.0 立体声", new[] { "FL", "FR" }),
        new("2.1", "2.1 立体声 + 低音", new[] { "FL", "FR", "LFE" }),
        new("3.0", "3.0 前置三声道", new[] { "FL", "FR", "FC" }),
        new("3.1", "3.1 前置 + 低音", new[] { "FL", "FR", "FC", "LFE" }),
        new("4.0", "4.0 四声道", new[] { "FL", "FR", "BL", "BR" }),
        new("4.1", "4.1 四声道 + 低音", new[] { "FL", "FR", "LFE", "BL", "BR" }),
        new("5.0", "5.0 五声道", new[] { "FL", "FR", "FC", "BL", "BR" }),
        new("5.1", "5.1 侧环绕", new[] { "FL", "FR", "FC", "LFE", "SL", "SR" }),
        new("5.1-back", "5.1 后环绕", new[] { "FL", "FR", "FC", "LFE", "BL", "BR" }),
        new("6.0", "6.0 后中置环绕", new[] { "FL", "FR", "FC", "BC", "SL", "SR" }),
        new("6.1", "6.1 后中置 + 低音", new[] { "FL", "FR", "FC", "LFE", "BC", "SL", "SR" }),
        new("7.0", "7.0 侧置 + 后置", new[] { "FL", "FR", "FC", "BL", "BR", "SL", "SR" }),
        new("7.1", "7.1 侧置 + 后置 + 低音", new[] { "FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR" })
    };
    public static SpeakerRole Role(string id) => Roles.First(r => r.Id == id);
    public static SpeakerLayout Get(string id) => All.FirstOrDefault(l => l.Id == id) ?? throw new InvalidOperationException("未知声道布局：" + id);
    public static string[] SourceRoles(Endpoint? d)
    {
        if (d == null) return Array.Empty<string>();
        string[] bits = { "FL", "FR", "FC", "LFE", "BL", "BR", "FLC", "FRC", "BC", "SL", "SR", "TC", "TFL", "TFC", "TFR", "TBL", "TBC", "TBR" };
        var found = bits.Where((_, n) => (d.Mask & (1 << n)) != 0).ToArray();
        return found.Length == d.Channels ? found : Enumerable.Range(1, d.Channels).Select(n => "CH" + n).ToArray();
    }
    public static string[] ChannelNames(Endpoint? d) => SourceRoles(d).Select((r, n) => $"{n + 1} · {r} {Roles.FirstOrDefault(s => s.Id == r)?.Name}").ToArray();
    public static int Match(Endpoint? d, SpeakerLayout layout, string role)
    {
        if (d == null) return -1;
        var source = SourceRoles(d);
        int index = Array.IndexOf(source, role);
        if (index >= 0) return index;
        // Only interchange side/back positions when the layout has one surround pair.
        // Never invent LFE/center audio, or duplicate a surround channel in 7.1.
        string alternate = role switch { "BL" => "SL", "BR" => "SR", "SL" => "BL", "SR" => "BR", _ => "" };
        if (alternate.Length > 0 && !layout.Roles.Contains(alternate)) return Array.IndexOf(source, alternate);
        return -1;
    }
    public static List<SpeakerSetting> Create(SpeakerLayout layout, Endpoint? source) =>
        layout.Roles.Select(r => new SpeakerSetting { Role = r, SourceChannel = Match(source, layout, r), Side = r is "FR" or "BR" or "SR" or "LFE" ? 1 : 0 }).ToList();

    public static List<SpeakerSetting> Resolve(SurroundProfile profile, Endpoint source)
    {
        var copy = JsonSerializer.Deserialize<List<SpeakerSetting>>(JsonSerializer.Serialize(profile.Speakers))!;
        if (profile.AutoMatchSource)
            foreach (var s in copy) s.SourceChannel = Match(source, Get(profile.LayoutId), s.Role);
        return copy;
    }

    public static List<Route> BuildRoutes(IReadOnlyList<SpeakerSetting> speakers, Endpoint source, IReadOnlyList<Endpoint> devices)
    {
        ValidateSettings(speakers);
        var assigned = speakers.Where(s => s.DeviceId.Length > 0).ToArray();
        if (assigned.GroupBy(s => (s.DeviceId, s.Side)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("多个音箱占用了同一设备的同一输出侧。请修改右侧“输出接口”，每个 L/R 只分配一个音箱。");
        foreach (var s in assigned)
        {
            if (s.DeviceId == source.Id) throw new InvalidOperationException("音源不能同时作为输出设备，否则会产生反馈。");
            var d = devices.FirstOrDefault(d => d.Id == s.DeviceId);
            if (d == null || d.Channels < 2) throw new InvalidOperationException($"{Role(s.Role).Name} 的输出设备已断开或不可用，请重新选择。");
            if (s.SourceChannel >= source.Channels) throw new InvalidOperationException($"{Role(s.Role).Name} 的音源声道不存在，请重新匹配。");
        }
        var active = assigned.Where(s => s.SourceChannel >= 0 && !s.Muted).ToArray();
        if (active.Length == 0) throw new InvalidOperationException("请至少为一个未静音音箱选择音源声道和输出设备。");
        return active.GroupBy(s => s.DeviceId).Select(g => {
            var l = g.FirstOrDefault(s => s.Side == 0); var r = g.FirstOrDefault(s => s.Side == 1);
            return new Route(g.Key, l?.SourceChannel ?? -1, r?.SourceChannel ?? -1, l?.LinearGain ?? 0, l?.DelayMs ?? 0, r?.LinearGain ?? 0, r?.DelayMs ?? 0);
        }).ToList();
    }
    public static void ValidateSettings(IReadOnlyList<SpeakerSetting> speakers)
    {
        if (speakers.Count > 8 || speakers.Any(s => s == null) || speakers.Select(s => s.Role).Distinct().Count() != speakers.Count)
            throw new InvalidOperationException("音箱配置重复或超出 8 声道。");
        foreach (var s in speakers)
            if (!Roles.Any(r => r.Id == s.Role) || s.SourceChannel < -1 || s.SourceChannel > 63 || s.DeviceId == null || s.Side is not (0 or 1) ||
                !float.IsFinite(s.GainDb) || s.GainDb < -60 || s.GainDb > 6 || s.DelayMs < 0 || s.DelayMs > 500)
                throw new InvalidOperationException("音箱配置包含无效的声道、增益或延迟。");
    }
    public static SurroundProfile ReadProfile(string json, IReadOnlyList<Endpoint> devices)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("Version", out _))
        {
            var p = JsonSerializer.Deserialize<SurroundProfile>(json) ?? throw new InvalidOperationException("配置为空。");
            if (p.Version != 2 || p.Speakers == null || p.SourceId == null) throw new InvalidOperationException("不支持此配置版本。");
            var layout = Get(p.LayoutId); ValidateSettings(p.Speakers);
            if (!p.Speakers.Select(s => s.Role).Order().SequenceEqual(layout.Roles.Order())) throw new InvalidOperationException("音箱列表与布局不符。");
            return p;
        }
        var old = JsonSerializer.Deserialize<Profile>(json) ?? throw new InvalidOperationException("旧配置为空。");
        if (old.Mode is not (4 or 5) || old.Routes == null || old.SourceId == null || old.Routes.Count > 3) throw new InvalidOperationException("不支持此旧配置。");
        var format = devices.FirstOrDefault(d => d.Id == old.SourceId);
        var oldLayout = Get(old.Mode == 4 ? "4.0" : "5.0");
        var settings = Create(oldLayout, format);
        string[] order = old.Mode == 4 ? new[] { "FL", "FR", "BL", "BR" } : new[] { "FL", "FR", "BL", "BR", "FC" };
        int n = 0;
        foreach (var r in old.Routes)
        {
            foreach (int side in new[] { 0, 1 })
            {
                int channel = side == 0 ? r.Left : r.Right;
                if (n >= order.Length) { if (channel >= 0) throw new InvalidOperationException("旧配置使用了额外输出，请在新版中手动设置。"); continue; }
                string roleId = order[n++];
                var s = settings.First(s => s.Role == roleId);
                s.SourceChannel = channel; s.DeviceId = r.DeviceId; s.Side = side; s.DelayMs = r.DelayMs;
                if (!float.IsFinite(r.Gain) || r.Gain < 0 || r.Gain > 1) throw new InvalidOperationException("旧配置音量无效。");
                s.Muted = r.Gain == 0; s.GainDb = r.Gain > 0 ? Math.Max(-60, 20 * MathF.Log10(r.Gain)) : -60;
            }
        }
        ValidateSettings(settings);
        return new SurroundProfile { SourceId = old.SourceId, LayoutId = oldLayout.Id, Speakers = settings };
    }
}
