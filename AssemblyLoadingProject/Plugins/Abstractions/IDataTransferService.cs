namespace AssemblyLoadingProject.Plugins.Abstractions;

/// <summary>
/// 插件契约：所有可被热插拔加载的数据传输服务插件都必须实现该接口。
/// 该接口会被编译进宿主程序集与插件程序集共享的"契约"中，是实现 AssemblyLoadContext
/// 热插拔的关键 —— 接口类型必须来自宿主（共享）程序集，而不能来自插件自身。
/// </summary>
public interface IDataTransferService
{
    /// <summary>插件的唯一标识（不可重复）。</summary>
    string Id { get; }

    /// <summary>插件显示名称。</summary>
    string DisplayName { get; }

    /// <summary>插件版本。</summary>
    string Version { get; }

    /// <summary>插件描述。</summary>
    string Description { get; }

    /// <summary>
    /// 在插件加载并完成初始化后调用。用于解析依赖注入、创建数据库连接等资源。
    /// </summary>
    void Initialize(PluginContext context);

    /// <summary>
    /// 单次执行的数据传输逻辑。由定时调度引擎周期性调用。
    /// </summary>
    Task<TransferResult> ExecuteAsync(PluginContext context, CancellationToken cancellationToken);

    /// <summary>插件卸载前调用，用于释放资源、关闭连接等。</summary>
    void Dispose();
}

/// <summary>
/// 传递给插件执行上下文，包含宿主提供的参数与能力。
/// 由于插件在独立的 AssemblyLoadContext 中，只能通过此共享类型与宿主通信。
/// </summary>
public sealed class PluginContext
{
    /// <summary>用户在前端配置、执行时传入的参数键值对。</summary>
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }

    /// <summary>宿主提供的日志记录器（来自宿主程序集）。</summary>
    public required Action<string, LogLevel> Logger { get; init; }

    /// <summary>本次执行的取消令牌。</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>日志级别。</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Critical = 5,
}

/// <summary>单次数据传输执行结果。</summary>
public sealed class TransferResult
{
    /// <summary>执行是否成功。</summary>
    public bool Success { get; init; }

    /// <summary>简要结果描述。</summary>
    public string? Message { get; init; }

    /// <summary>传输的数据行数（可选）。</summary>
    public long? RowsAffected { get; init; }

    /// <summary>耗时（毫秒）。</summary>
    public long ElapsedMilliseconds { get; init; }

    public static TransferResult Ok(string? message = null, long? rowsAffected = null, long elapsedMs = 0)
        => new() { Success = true, Message = message, RowsAffected = rowsAffected, ElapsedMilliseconds = elapsedMs };

    public static TransferResult Fail(string? message, long elapsedMs = 0)
        => new() { Success = false, Message = message, ElapsedMilliseconds = elapsedMs };
}
