namespace AssemblyLoadingProject.Plugins;

/// <summary>
/// 失败重试策略。
/// 定义插件执行失败后，宿主如何安排"重试下一次执行"。
/// </summary>
public enum RetryStrategy
{
    /// <summary>不重试：失败后按正常调度执行下一次。</summary>
    None = 0,

    /// <summary>固定间隔重试：每隔 <see cref="RetryConfig.RetryIntervalSeconds"/> 秒重试一次。</summary>
    FixedInterval = 1,

    /// <summary>
    /// 指数退避重试：第 n 次失败后延迟 = 基础间隔 × 退避因子^(n-1)，直到最大次数。
    /// </summary>
    ExponentialBackoff = 2,

    /// <summary>指定次数重试：在较短时间内连续重试 <see cref="RetryConfig.MaxRetryCount"/> 次。</summary>
    FixedCount = 3,
}

/// <summary>
/// 失败重试配置，随 <see cref="PluginConfig.Retry"/> 持久化。
/// </summary>
public sealed class RetryConfig
{
    /// <summary>重试策略。</summary>
    public RetryStrategy Strategy { get; set; } = RetryStrategy.None;

    /// <summary>
    /// 重试间隔（秒）。用于 <see cref="RetryStrategy.FixedInterval"/>；
    /// 作为 <see cref="RetryStrategy.ExponentialBackoff"/> 的基础间隔。
    /// </summary>
    public int RetryIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// 最大重试次数。用于 <see cref="RetryStrategy.FixedCount"/>，
    /// 也是 <see cref="RetryStrategy.ExponentialBackoff"/> 的重试次数上限。
    /// &lt;=0 表示不限制次数（需配合调度，避免无限重试）。
    /// </summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>指数退避因子（<see cref="RetryStrategy.ExponentialBackoff"/> 使用，默认 2）。</summary>
    public double BackoffFactor { get; set; } = 2.0;

    /// <summary>快捷构造：固定间隔重试。</summary>
    public static RetryConfig FixedIntervalRetry(int seconds)
        => new() { Strategy = RetryStrategy.FixedInterval, RetryIntervalSeconds = Math.Max(seconds, 1) };

    /// <summary>快捷构造：指数退避重试。</summary>
    public static RetryConfig ExponentialRetry(int baseSeconds, int maxCount, double factor = 2.0)
        => new()
        {
            Strategy = RetryStrategy.ExponentialBackoff,
            RetryIntervalSeconds = Math.Max(baseSeconds, 1),
            MaxRetryCount = maxCount,
            BackoffFactor = factor,
        };

    /// <summary>快捷构造：指定次数重试（用较短间隔快速重试）。</summary>
    public static RetryConfig CountRetry(int maxCount, int intervalSeconds = 30)
        => new()
        {
            Strategy = RetryStrategy.FixedCount,
            MaxRetryCount = Math.Max(maxCount, 0),
            RetryIntervalSeconds = Math.Max(intervalSeconds, 1),
        };
}
