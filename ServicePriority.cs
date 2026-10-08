using System.Diagnostics;

namespace ChannelBridge;

public static class ServicePriority
{
    public static readonly (string Id, string Label)[] Options = {
        ("Idle", "低优先级"), ("BelowNormal", "低于正常"), ("Normal", "正常（默认）"),
        ("AboveNormal", "高于正常"), ("High", "高优先级")
    };

    public static ProcessPriorityClass Parse(string value) => value switch
    {
        "Idle" => ProcessPriorityClass.Idle,
        "BelowNormal" => ProcessPriorityClass.BelowNormal,
        "Normal" => ProcessPriorityClass.Normal,
        "AboveNormal" => ProcessPriorityClass.AboveNormal,
        "High" => ProcessPriorityClass.High,
        _ => throw new InvalidDataException("无效的后台服务优先级。")
    };

    public static void Apply(string value)
    {
        using var process = Process.GetCurrentProcess();
        process.PriorityClass = Parse(value);
        process.Refresh();
        if (process.PriorityClass != Parse(value)) throw new IOException("后台服务优先级未生效。");
    }
}
