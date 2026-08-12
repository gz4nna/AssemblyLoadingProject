using System.Text.Json.Serialization;
using LogLevel = AssemblyLoadingProject.Plugins.Abstractions.LogLevel;

namespace AssemblyLoadingProject.Plugins;

/// <summary>插件当前的运行状态。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginStatus
{
    /// <summary>已扫描到 DLL 但未加载。</summary>
    Discovered,
    /// <summary>已加载到 AssemblyLoadContext，可被调度。</summary>
    Loaded,
    /// <summary>正在执行。</summary>
    Running,
    /// <summary>已停止调度（用户禁用或卸载）。</summary>
    Stopped,
    /// <summary>加载或执行过程中发生错误。</summary>
    Faulted,
    /// <summary>已从内存卸载。</summary>
    Unloaded,
}

/// <summary>插件运行时状态快照，用于前端展示。</summary>
public sealed class PluginRunState
{
    /// <summary>插件 Id。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名称。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>版本。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>描述。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>状态。</summary>
    public PluginStatus Status { get; set; } = PluginStatus.Discovered;

    /// <summary>当前状态说明。</summary>
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>DLL 文件名。</summary>
    public string AssemblyFile { get; set; } = string.Empty;

    /// <summary>是否已启用调度。</summary>
    public bool Enabled { get; set; }

    /// <summary>下次计划执行时间。</summary>
    public DateTimeOffset? NextRunAt { get; set; }

    /// <summary>最近一次开始执行时间。</summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>最近一次执行耗时（毫秒）。</summary>
    public long LastElapsedMs { get; set; }

    /// <summary>最近一次执行是否成功。</summary>
    public bool? LastSuccess { get; set; }

    /// <summary>最近一次执行消息。</summary>
    public string? LastMessage { get; set; }

    /// <summary>累计成功次数。</summary>
    public long SuccessCount { get; set; }

    /// <summary>累计失败次数。</summary>
    public long FailCount { get; set; }

    /// <summary>当前连续重试计数（运行时维护，失败递增、成功或策略耗尽后清零）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int RetryCount { get; set; }

    private readonly object _logLock = new();
    private readonly List<PluginLogEntry> _logs = new();
    private const int MaxLogs = 50; // 仅内存保留最近日志，长期信息走落盘

    /// <summary>日志写盘回调（由宿主注入，未注入则不落盘）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Action<string, LogLevel>? PersistLogAction { get; set; }

    /// <summary>最近一段时间内的日志（仅用于直接展示）。经 <see cref="AddLog"/> / <see cref="GetLogSnapshotReversed"/> 访问。</summary>
    public IReadOnlyList<PluginLogEntry> Logs => _logs;

    /// <summary>追加一条日志（线程安全，后台调度线程调用）。超过上限时保留最近条目，并交回执到落盘。</summary>
    public void AddLog(DateTimeOffset time, string level, string message)
    {
        lock (_logLock)
        {
            _logs.Add(new PluginLogEntry { Time = time, Level = level, Message = message });
            if (_logs.Count > MaxLogs)
                _logs.RemoveRange(0, _logs.Count - MaxLogs);
        }
        PersistLogAction?.Invoke(message, Enum.TryParse(level, out LogLevel lv) ? lv : LogLevel.Info);
    }

    /// <summary>获取日志倒序快照（线程安全，UI 线程调用，避免枚举时被修改）。</summary>
    public List<PluginLogEntry> GetLogSnapshotReversed()
    {
        lock (_logLock)
        {
            var copy = new List<PluginLogEntry>(_logs);
            copy.Reverse();
            return copy;
        }
    }

    // ===== 执行历史（冗余汇总） =====
    private readonly object _historyLock = new();
    private readonly List<ExecutionRecord> _history = new();
    private const int MaxHistory = 20; // 状态页只展示最近 N 次执行汇总

    /// <summary>最近若干次执行的汇总记录（供状态页以"列表"而非"大量日志"呈现）。</summary>
    public IReadOnlyList<ExecutionRecord> ExecutionHistory => _history;

    /// <summary>记录一次执行汇总（线程安全）。</summary>
    public void AddExecutionRecord(DateTimeOffset time, bool success, long elapsedMs, long? rowsAffected, string? message)
    {
        lock (_historyLock)
        {
            _history.Add(new ExecutionRecord
            {
                Time = time,
                Success = success,
                ElapsedMs = elapsedMs,
                RowsAffected = rowsAffected,
                Message = message,
            });
            if (_history.Count > MaxHistory)
                _history.RemoveAt(0);
        }
    }

    /// <summary>获取执行历史倒序快照（线程安全）。</summary>
    public List<ExecutionRecord> GetExecutionHistoryReversed()
    {
        lock (_historyLock)
        {
            var copy = new List<ExecutionRecord>(_history);
            copy.Reverse();
            return copy;
        }
    }

    /// <summary>DLL 文件最后写入时间（用于检测文件更新以触发热重载）。</summary>
    public DateTime? FileLastWriteTime { get; set; }
}

/// <summary>一条执行日志。</summary>
public sealed class PluginLogEntry
{
    public DateTimeOffset Time { get; set; }
    public string Level { get; set; } = "Info";
    public string Message { get; set; } = string.Empty;
}

/// <summary>一次执行的汇总记录（冗余信息汇总保存，避免前端堆积大量细节日志）。</summary>
public sealed class ExecutionRecord
{
    public DateTimeOffset Time { get; set; }
    public bool Success { get; set; }
    public long ElapsedMs { get; set; }
    public long? RowsAffected { get; set; }
    public string? Message { get; set; }
}
