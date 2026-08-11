namespace AssemblyLoadingProject.Plugins;

/// <summary>
/// 任务启动条件（调度模式）。
/// 支持多样化的启动方式，涵盖"精确时间"、"固定间隔"、"精确时间段内固定间隔"等。
/// </summary>
public enum ScheduleMode
{
    /// <summary>固定间隔（按 <see cref="ScheduleConfig.IntervalSeconds"/> 周期执行）。</summary>
    FixedInterval = 0,

    /// <summary>
    /// Cron 表达式（按 <see cref="ScheduleConfig.Cron"/> 执行），支持精确到秒。
    /// 例："0 30 14 * * ?" 表示每天 14:30:00。
    /// </summary>
    Cron = 1,

    /// <summary>
    /// 多个精确时间（按 <see cref="ScheduleConfig.Times"/> 每天指定时刻执行）。
    /// 例：["09:00", "14:30", "18:00"]。
    /// </summary>
    Times = 2,

    /// <summary>
    /// 精确时间段内固定间隔（在 <see cref="ScheduleConfig.WindowStart"/> 到
    /// <see cref="ScheduleConfig.WindowEnd"/> 之间，按 <see cref="ScheduleConfig.IntervalSeconds"/> 周期执行）。
    /// 例：窗口 "09:00"~"17:00"，间隔 600 秒 → 每天 9 点到 17 点每 10 分钟一次。
    /// </summary>
    IntervalWithinWindow = 3,
}

/// <summary>
/// 插件的调度配置。
/// 定义了插件以何种方式被触发执行。由 <see cref="PluginConfig.Schedule"/> 引用，
/// 并随插件配置一起持久化到 JSON。
/// </summary>
public sealed class ScheduleConfig
{
    /// <summary>调度模式。</summary>
    public ScheduleMode Mode { get; set; } = ScheduleMode.FixedInterval;

    /// <summary>
    /// 固定间隔（秒）。用于 <see cref="ScheduleMode.FixedInterval"/> 与
    /// <see cref="ScheduleMode.IntervalWithinWindow"/> 两种模式。
    /// </summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Cron 表达式（<see cref="ScheduleMode.Cron"/> 模式使用）。
    /// 支持标准 6 段（含秒）或 5 段（不含秒）格式。
    /// </summary>
    public string? Cron { get; set; }

    /// <summary>
    /// 多个精确时间（<see cref="ScheduleMode.Times"/> 模式使用），格式 "HH:mm" 或 "HH:mm:ss"。
    /// 每天按列表中的时刻各执行一次。
    /// </summary>
    public List<string> Times { get; set; } = new();

    /// <summary>时间窗口起始（<see cref="ScheduleMode.IntervalWithinWindow"/> 使用），格式 "HH:mm"。</summary>
    public string? WindowStart { get; set; }

    /// <summary>时间窗口结束（<see cref="ScheduleMode.IntervalWithinWindow"/> 使用），格式 "HH:mm"。</summary>
    public string? WindowEnd { get; set; }

    /// <summary>用固定间隔快捷构造一个调度配置。</summary>
    public static ScheduleConfig FixedInterval(int seconds)
        => new() { Mode = ScheduleMode.FixedInterval, IntervalSeconds = seconds };

    /// <summary>用 Cron 表达式快捷构造一个调度配置。</summary>
    public static ScheduleConfig FromCron(string cron)
        => new() { Mode = ScheduleMode.Cron, Cron = cron };

    /// <summary>用多个精确时间快捷构造一个调度配置。</summary>
    public static ScheduleConfig FromTimes(params string[] times)
        => new() { Mode = ScheduleMode.Times, Times = times.ToList() };

    /// <summary>用"时间段内固定间隔"快捷构造一个调度配置。</summary>
    public static ScheduleConfig InWindow(string start, string end, int intervalSeconds)
        => new()
        {
            Mode = ScheduleMode.IntervalWithinWindow,
            WindowStart = start,
            WindowEnd = end,
            IntervalSeconds = intervalSeconds,
        };
}
