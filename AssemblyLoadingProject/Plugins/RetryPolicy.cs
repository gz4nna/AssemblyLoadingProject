namespace AssemblyLoadingProject.Plugins;

/// <summary>
/// 重试策略计算器。
/// 根据 <see cref="RetryConfig"/> 与当前已重试次数，计算下一次重试应在什么时间进行，
/// 以及是否应继续重试（还是放弃重试、回到正常调度）。
/// </summary>
public static class RetryPolicy
{
    /// <summary>
    /// 计算失败后下一次应执行（重试）的时间。
    /// </summary>
    /// <param name="config">重试配置。</param>
    /// <param name="failCount">已失败（含本次）的次数，用于指数退避。</param>
    /// <param name="now">当前时间。</param>
    /// <returns>应重试的时间；返回 null 表示不重试（按正常调度）。</returns>
    public static DateTimeOffset? GetNextRetryAt(RetryConfig config, int failCount, DateTimeOffset now)
    {
        if (config == null || config.Strategy == RetryStrategy.None)
            return null;

        switch (config.Strategy)
        {
            case RetryStrategy.FixedInterval:
                return now.AddSeconds(Math.Max(config.RetryIntervalSeconds, 1));

            case RetryStrategy.ExponentialBackoff:
                // 超出最大次数则不再重试
                if (config.MaxRetryCount > 0 && failCount > config.MaxRetryCount)
                    return null;
                // 退避延迟 = 基础间隔 × 因子^(次数-1)，并封顶避免无限增大
                var exponent = Math.Max(0, failCount - 1);
                var delay = config.RetryIntervalSeconds * Math.Pow(Math.Max(config.BackoffFactor, 1), exponent);
                var cap = 24 * 3600; // 封顶 1 天
                delay = Math.Min(delay, cap);
                return now.AddSeconds(delay);

            case RetryStrategy.FixedCount:
                // 达到最大次数后不再重试
                if (config.MaxRetryCount > 0 && failCount >= config.MaxRetryCount)
                    return null;
                return now.AddSeconds(Math.Max(config.RetryIntervalSeconds, 1));

            default:
                return null;
        }
    }

    /// <summary>判断是否仍允许继续重试（供状态提示用）。</summary>
    public static bool CanRetry(RetryConfig config, int failCount)
    {
        if (config == null || config.Strategy == RetryStrategy.None)
            return false;

        switch (config.Strategy)
        {
            case RetryStrategy.FixedInterval:
                return true; // 固定间隔无限重试（直到成功或被禁用）
            case RetryStrategy.ExponentialBackoff:
                return config.MaxRetryCount <= 0 || failCount <= config.MaxRetryCount;
            case RetryStrategy.FixedCount:
                return config.MaxRetryCount <= 0 || failCount < config.MaxRetryCount;
            default:
                return false;
        }
    }
}
