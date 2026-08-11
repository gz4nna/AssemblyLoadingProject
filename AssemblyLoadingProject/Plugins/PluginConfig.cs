namespace AssemblyLoadingProject.Plugins;

/// <summary>
/// 插件配置模型。
/// 记录一个插件是否启用、调度条件（<see cref="Schedule"/>）以及前端配置的参数字典。
/// 由 <see cref="PluginConfigStore"/> 持久化为 JSON，宿主重启后自动恢复。
/// </summary>
public sealed class PluginConfig
{
    /// <summary>对应插件 DLL 文件名（含扩展名），作为关联主键。</summary>
    public required string AssemblyFile { get; set; }

    /// <summary>是否启用（启用后按 <see cref="Schedule"/> 定时执行）。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 调度配置：精确时间 / 固定间隔 / Cron / 时间段内固定间隔。
    /// 为空时回退到 <see cref="IntervalSeconds"/> 的固定间隔语义。
    /// </summary>
    public ScheduleConfig? Schedule { get; set; }

    /// <summary>
    /// 执行间隔（秒）。兼容旧配置：当 <see cref="Schedule"/> 为 null 时作为固定间隔使用。
    /// &lt;=0 表示不按固定间隔执行，仅手动触发。
    /// </summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// 兼容旧配置的 Cron 表达式。当 <see cref="Schedule"/> 为 null 且本字段非空时，按 Cron 调度。
    /// 建议改用 <see cref="Schedule"/> 以使用更多调度模式。
    /// </summary>
    public string? Cron { get; set; }

    /// <summary>失败重试配置（失败时按策略安排重试，见 <see cref="RetryConfig"/>）。</summary>
    public RetryConfig? Retry { get; set; }

    /// <summary>执行时传给插件的参数键值对（由前端编辑）。</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>前端可编辑的备注/说明（持久化，便于标记用途）。</summary>
    public string? Notes { get; set; }

    /// <summary>最近一次执行状态快照（运行时维护，不持久化）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PluginRunState? LastRun { get; set; }

    /// <summary>取得实际生效的调度配置（无则按旧字段合成固定间隔）。</summary>
    public ScheduleConfig EffectiveSchedule
        => Schedule ?? new ScheduleConfig
        {
            Mode = string.IsNullOrWhiteSpace(Cron) ? ScheduleMode.FixedInterval : ScheduleMode.Cron,
            IntervalSeconds = Math.Max(IntervalSeconds, 1),
            Cron = Cron,
        };
}
