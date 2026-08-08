namespace AssemblyLoadingProject.Plugins;

/// <summary>
/// 插件配置模型。
/// 记录一个插件是否启用、执行频率（间隔/Cron）以及前端配置的参数字典。
/// 由 <see cref="PluginConfigStore"/> 持久化为 JSON，宿主重启后自动恢复。
/// </summary>
public sealed class PluginConfig
{
    /// <summary>对应插件 DLL 文件名（含扩展名），作为关联主键。</summary>
    public required string AssemblyFile { get; set; }

    /// <summary>是否启用（启用后按 <see cref="Cron"/> 或 <see cref="IntervalSeconds"/> 定时执行）。</summary>
    public bool Enabled { get; set; }

    /// <summary>执行间隔（秒）。&lt;=0 表示不按固定间隔执行，仅手动触发。</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// 可选的 Cron 表达式（如 "0 * * * * ?"）。若填写则优先于 IntervalSeconds 使用。
    /// 本实现使用定时器轮询，简单场景直接使用 IntervalSeconds 即可。
    /// </summary>
    public string? Cron { get; set; }

    /// <summary>执行时传给插件的参数键值对（由前端编辑）。</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>前端可编辑的备注/说明（持久化，便于标记用途）。</summary>
    public string? Notes { get; set; }

    /// <summary>最近一次执行状态快照（运行时维护，不持久化）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PluginRunState? LastRun { get; set; }
}
