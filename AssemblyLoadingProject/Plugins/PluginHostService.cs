using System.Collections.Concurrent;
using System.Diagnostics;
using AssemblyLoadingProject.Plugins.Abstractions;
using Microsoft.Extensions.Logging;
using LogLevel = AssemblyLoadingProject.Plugins.Abstractions.LogLevel;

namespace AssemblyLoadingProject.Plugins;

/// <summary>
/// 插件宿主服务：作为单例注册到 DI。
/// 负责：
///  - 周期扫描插件目录（发现新 DLL / 检测更新）；
///  - 按配置加载/卸载插件（加载延迟到参数就绪并手动触发，见 <see cref="LoadAndStart"/>）；
///  - 定时调度已启用插件（基于 <see cref="PluginConfig.IntervalSeconds"/> 的轮询调度）；
///  - 维护运行时状态 <see cref="PluginRunState"/> 供前端展示。
/// </summary>
public sealed class PluginHostService : IDisposable
{
    private readonly ILogger<PluginHostService> _logger;
    private readonly PluginAssemblyLoader _loader;
    private readonly PluginConfigStore _configStore;
    private readonly PluginLogStore _logStore;
    private readonly AlertService _alertService;
    private readonly AppSettingsStore _appSettings;
    private readonly object _lock = new();

    // assemblyFile -> 配置（内存副本；启用状态/参数会持久化到 JSON）
    private readonly ConcurrentDictionary<string, PluginConfig> _configs = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _running;
    private DateTimeOffset _lastScan = DateTimeOffset.MinValue;

    public PluginHostService(
        ILogger<PluginHostService> logger,
        string pluginsDirectory,
        AppSettingsStore appSettings,
        AlertService alertService)
    {
        _logger = logger;
        _loader = new PluginAssemblyLoader(logger, pluginsDirectory);
        _configStore = new PluginConfigStore(pluginsDirectory);
        _logStore = new PluginLogStore(pluginsDirectory);
        _appSettings = appSettings;
        _alertService = alertService;
    }

    /// <summary>全局应用设置（告警地址、邮箱等，存于 SQLite）。</summary>
    public AppSettingsStore AppSettings => _appSettings;

    /// <summary>全局应用设置访问（供前端读写告警配置）。</summary>
    public Dictionary<string, string> GetAppSettings() => _appSettings.LoadAll();

    /// <summary>日志存储目录。</summary>
    public string LogDirectory => _logStore.LogDirectory;

    /// <summary>读取某插件的持久化历史日志（最近 N 行）。</summary>
    public List<string> ReadLogHistory(string assemblyFile, int maxLines = 100)
        => _logStore.ReadHistory(assemblyFile, maxLines);

    /// <summary>所有插件的运行时状态快照（供 UI 绑定）。</summary>
    public IReadOnlyList<PluginRunState> GetAllStates()
    {
        lock (_lock)
        {
            return _loader.Slots
                .OrderBy(s => s.AssemblyFile, StringComparer.OrdinalIgnoreCase)
                .Select(s => s.State)
                .ToList();
        }
    }

    /// <summary>启动后台调度循环。</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
            _logger.LogInformation("插件宿主服务已启动");
        }
    }

    /// <summary>停止后台调度循环。</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!_running) return;
            _running = false;
            _cts?.Cancel();
            _loopTask?.GetAwaiter().GetResult();
            _cts?.Dispose();
            _cts = null;
            _logger.LogInformation("插件宿主服务已停止");
        }
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // 1) 周期扫描（每 10 秒检查一次）
                if (DateTimeOffset.UtcNow - _lastScan > TimeSpan.FromSeconds(10))
                {
                    ScanPlugins();
                    _lastScan = DateTimeOffset.UtcNow;
                }

                // 2) 检测待重载插件
                HandleReloads();

                // 3) 调度执行到期的插件
                await RunDuePluginsAsync(token);

                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "插件调度循环异常");
        }
    }

    /// <summary>
    /// 手动触发一次目录扫描（UI 的"刷新"按钮）。
    /// 扫描后：为新增 DLL 建立默认配置；并从持久化 JSON 中把历史配置覆盖到默认值上。
    /// </summary>
    public void ScanPlugins()
    {
        _loader.Scan();

        // 读取已持久化配置（若文件存在），用于覆盖默认值
        var persisted = _configStore.Load();

        foreach (var slot in _loader.Slots)
        {
            // 0) 注入日志落盘回调：每条日志在保留近期快照的同时写入插件专属文件
            if (slot.State.PersistLogAction == null)
            {
                var file = slot.AssemblyFile;
                slot.State.PersistLogAction = (msg, lv) => _logStore.LogToFile(file, msg, lv, DateTimeOffset.Now);
            }

            // 1) 若无默认配置则新建
            _configs.TryAdd(slot.AssemblyFile, new PluginConfig { AssemblyFile = slot.AssemblyFile });

            // 2) 若有历史配置，覆盖默认值（保留前端已配置的参数/启用状态/间隔）
            if (persisted.TryGetValue(slot.AssemblyFile, out var saved))
            {
                var cfg = _configs[slot.AssemblyFile];
                cfg.Enabled = saved.Enabled;
                cfg.IntervalSeconds = saved.IntervalSeconds;
                cfg.Cron = saved.Cron;
                cfg.Schedule = saved.Schedule;
                cfg.Parameters = saved.Parameters ?? new Dictionary<string, string>();
                cfg.Notes = saved.Notes;
            }
        }
    }

    /// <summary>
    /// 宿主启动时调用：扫描后，把配置中 Enabled=true 的插件自动加载并纳入调度。
    /// 即"配置中写了启动状态开的，就去调用它"。
    /// </summary>
    public void LoadPersistedAndStart()
    {
        ScanPlugins();
        foreach (var file in _configs.Keys)
        {
            var cfg = _configs[file];
            if (cfg.Enabled)
            {
                _logger.LogInformation("按持久化配置自动启动插件: {File}", file);
                LoadAndStart(file, cfg);
            }
        }
    }

    /// <summary>处理标记为需要重载的插件。</summary>
    private void HandleReloads()
    {
        foreach (var slot in _loader.Slots.Where(s => s.NeedsReload))
        {
            var cfg = GetOrCreateConfig(slot.AssemblyFile);
            // 若启用且已配置参数则自动重载；否则仅卸载并保持 Discovered
            if (cfg.Enabled && slot.Instance != null)
            {
                _logger.LogInformation("重载插件: {File}", slot.AssemblyFile);
                LoadAndStart(slot.AssemblyFile, cfg);
            }
            else
            {
                _loader.Unload(slot.AssemblyFile);
                slot.State.Status = PluginStatus.Discovered;
                slot.State.StatusMessage = "文件已更新，待重新加载";
            }
        }
    }

    private PluginConfig GetOrCreateConfig(string assemblyFile)
        => _configs.GetOrAdd(assemblyFile, f => new PluginConfig { AssemblyFile = f });

    /// <summary>
    /// 依据插件调度配置计算下一次执行时间；无可用时间则回退为固定间隔。
    /// 使用 <see cref="ScheduleEvaluator"/> 支持精确时间 / Cron / 时间段内固定间隔等模式。
    /// </summary>
    private static DateTimeOffset ComputeNextRunAt(PluginConfig config, DateTimeOffset from)
    {
        var schedule = config.EffectiveSchedule;
        if (!ScheduleEvaluator.Validate(schedule).Ok)
        {
            // 调度配置无效时回退为固定间隔，避免插件"卡死"不再执行
            return from.AddSeconds(Math.Max(config.IntervalSeconds, 1));
        }
        return ScheduleEvaluator.GetNextRunAt(schedule, from)
            ?? from.AddSeconds(Math.Max(config.IntervalSeconds, 1));
    }

    /// <summary>
    /// 加载插件并（若启用）注册调度。此方法在前端设置好参数后由 UI 触发。
    /// 加载后不会立即执行，只有到期的定时任务才会触发执行。
    /// </summary>
    public bool LoadAndStart(string assemblyFile, PluginConfig? config = null)
    {
        var cfg = config ?? GetOrCreateConfig(assemblyFile);
        cfg.AssemblyFile = assemblyFile;

        var instance = _loader.Load(assemblyFile);
        if (instance == null)
            return false;

        // 初始化插件（传入上下文，插件可解析参数建立连接）
        instance.Initialize(new PluginContext
        {
            Parameters = cfg.Parameters,
            Logger = (msg, lv) => LogToState(assemblyFile, msg, lv),
            CancellationToken = CancellationToken.None,
        });

        var state = _loader.Slots.First(s => string.Equals(s.AssemblyFile, assemblyFile, StringComparison.OrdinalIgnoreCase)).State;
        state.Enabled = cfg.Enabled;
        if (cfg.Enabled)
        {
            state.Status = PluginStatus.Loaded;
            state.StatusMessage = "已加载，等待定时调度";
            state.NextRunAt = ComputeNextRunAt(cfg, DateTimeOffset.UtcNow);
        }
        else
        {
            state.Status = PluginStatus.Stopped;
            state.StatusMessage = "已加载，但未启用调度";
            state.NextRunAt = null;
        }

        _logger.LogInformation("插件已加载并注册调度: {File} (Enabled={Enabled})", assemblyFile, cfg.Enabled);
        return true;
    }

    /// <summary>卸载插件并停止其调度（同时持久化"未启用"状态）。</summary>
    public void UnloadPlugin(string assemblyFile)
    {
        if (_configs.TryGetValue(assemblyFile, out var cfg))
            cfg.Enabled = false;
        _loader.Unload(assemblyFile);
        PersistAll();
        _logger.LogInformation("插件已卸载: {File}", assemblyFile);
    }

    /// <summary>把当前全部配置写入持久化文件。</summary>
    private void PersistAll()
    {
        try
        {
            _configStore.Save(_configs.Values);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "持久化插件配置失败");
        }
    }

    /// <summary>
    /// 更新插件配置（参数、间隔、启用状态）。
    /// 前端改动后立即持久化到 JSON；若启用则立刻加载/重新初始化，纳入调度。
    /// </summary>
    public void UpdateConfig(string assemblyFile, PluginConfig config)
    {
        _configs[assemblyFile] = config;

        // 立即写盘持久化：前端点启用/保存，立刻将配置状态写入持久化文件
        PersistAll();

        var slot = _loader.Slots.FirstOrDefault(s => string.Equals(s.AssemblyFile, assemblyFile, StringComparison.OrdinalIgnoreCase));
        if (slot?.Instance != null)
        {
            // 重新初始化以应用新参数
            slot.Instance.Initialize(new PluginContext
            {
                Parameters = config.Parameters,
                Logger = (msg, lv) => LogToState(assemblyFile, msg, lv),
                CancellationToken = CancellationToken.None,
            });

            slot.State.Enabled = config.Enabled;
            if (config.Enabled)
            {
                slot.State.Status = PluginStatus.Loaded;
                slot.State.StatusMessage = "参数已更新，等待定时调度";
                slot.State.NextRunAt = ComputeNextRunAt(config, DateTimeOffset.UtcNow);
            }
            else
            {
                slot.State.Status = PluginStatus.Stopped;
                slot.State.StatusMessage = "已停止调度";
                slot.State.NextRunAt = null;
            }
        }
        else
        {
            // 未加载但用户设置启用了 → 立即加载（并纳入调度）
            if (config.Enabled)
                LoadAndStart(assemblyFile, config);
        }
    }

    /// <summary>手动立即执行一次指定插件。</summary>
    public async Task<TransferResult?> RunOnceAsync(string assemblyFile)
    {
        var slot = _loader.Slots.FirstOrDefault(s => string.Equals(s.AssemblyFile, assemblyFile, StringComparison.OrdinalIgnoreCase));
        if (slot?.Instance == null)
        {
            // 未加载则先加载（使用当前配置）
            var cfg = GetOrCreateConfig(assemblyFile);
            if (!LoadAndStart(assemblyFile, cfg))
                return null;
            slot = _loader.Slots.First(s => string.Equals(s.AssemblyFile, assemblyFile, StringComparison.OrdinalIgnoreCase));
        }

        var config = GetOrCreateConfig(assemblyFile);
        return await ExecutePluginAsync(slot, config);
    }

    /// <summary>调度执行到期的插件。</summary>
    private async Task RunDuePluginsAsync(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var slot in _loader.Slots)
        {
            var cfg = GetOrCreateConfig(slot.AssemblyFile);
            if (!cfg.Enabled || slot.Instance == null)
                continue;

            if (slot.State.NextRunAt == null)
            {
                slot.State.NextRunAt = ComputeNextRunAt(cfg, now);
                continue;
            }

            if (now >= slot.State.NextRunAt.Value)
            {
                // 防重入：如果正在执行则跳过本周期
                if (slot.State.Status == PluginStatus.Running)
                    continue;

                await ExecutePluginAsync(slot, cfg);
            }
        }
    }

    private async Task<TransferResult?> ExecutePluginAsync(PluginLoadSlot slot, PluginConfig config)
    {
        var state = slot.State;
        state.Status = PluginStatus.Running;
        state.StatusMessage = "执行中…";
        state.LastRunAt = DateTimeOffset.UtcNow;

        var sw = Stopwatch.StartNew();
        try
        {
            // 为每次执行创建独立取消令牌，支持超时与停止
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));

            var context = new PluginContext
            {
                Parameters = config.Parameters,
                Logger = (msg, lv) => LogToState(slot.AssemblyFile, msg, lv),
                CancellationToken = cts.Token,
            };

            var result = await slot.Instance!.ExecuteAsync(context, cts.Token);

            sw.Stop();
            state.LastElapsedMs = sw.ElapsedMilliseconds;
            state.LastSuccess = result.Success;
            state.LastMessage = result.Message;

            if (result.Success)
            {
                state.SuccessCount++;
                state.Status = PluginStatus.Loaded;
                state.StatusMessage = result.RowsAffected.HasValue
                    ? $"执行成功，影响 {result.RowsAffected} 行"
                    : $"执行成功：{result.Message}";
                LogToState(slot.AssemblyFile, $"执行成功：{result.Message}（耗时 {sw.ElapsedMilliseconds}ms）", LogLevel.Info);
                state.AddExecutionRecord(DateTimeOffset.Now, true, sw.ElapsedMilliseconds, result.RowsAffected, result.Message);
            }
            else
            {
                state.FailCount++;
                state.Status = PluginStatus.Faulted;
                state.StatusMessage = $"执行失败：{result.Message}";
                LogToState(slot.AssemblyFile, $"执行失败：{result.Message}", LogLevel.Error);
                state.AddExecutionRecord(DateTimeOffset.Now, false, sw.ElapsedMilliseconds, result.RowsAffected, result.Message);
                // 失败告警：发送邮件/企业微信（按全局设置开关）
                await _alertService.SendFailureAlertAsync(slot.AssemblyFile, result.Message ?? "执行失败", CancellationToken.None);
            }

            // 依据调度配置计算下次执行时间
            state.NextRunAt = ComputeNextRunAt(config, DateTimeOffset.UtcNow);

            return result;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            state.FailCount++;
            state.Status = PluginStatus.Faulted;
            state.StatusMessage = "执行被取消或超时";
            state.LastElapsedMs = sw.ElapsedMilliseconds;
            state.LastSuccess = false;
            state.NextRunAt = ComputeNextRunAt(config, DateTimeOffset.UtcNow);
            LogToState(slot.AssemblyFile, "执行被取消或超时", LogLevel.Warn);
            state.AddExecutionRecord(DateTimeOffset.Now, false, sw.ElapsedMilliseconds, null, "执行被取消或超时");
            await _alertService.SendFailureAlertAsync(slot.AssemblyFile, "执行被取消或超时", CancellationToken.None);
            return TransferResult.Fail("执行被取消或超时");
        }
        catch (Exception ex)
        {
            sw.Stop();
            state.FailCount++;
            state.Status = PluginStatus.Faulted;
            state.StatusMessage = $"异常：{ex.Message}";
            state.LastElapsedMs = sw.ElapsedMilliseconds;
            state.LastSuccess = false;
            state.NextRunAt = ComputeNextRunAt(config, DateTimeOffset.UtcNow);
            LogToState(slot.AssemblyFile, $"异常：{ex}", LogLevel.Error);
            state.AddExecutionRecord(DateTimeOffset.Now, false, sw.ElapsedMilliseconds, null, ex.Message);
            await _alertService.SendFailureAlertAsync(slot.AssemblyFile, ex.Message, CancellationToken.None);
            return TransferResult.Fail(ex.Message);
        }
    }

    private void LogToState(string assemblyFile, string message, LogLevel level)
    {
        var slot = _loader.Slots.FirstOrDefault(s => string.Equals(s.AssemblyFile, assemblyFile, StringComparison.OrdinalIgnoreCase));
        if (slot == null) return;

        slot.State.AddLog(DateTimeOffset.Now, level.ToString(), message);
    }

    /// <summary>获取指定插件的配置（供前端编辑）。</summary>
    public PluginConfig GetConfig(string assemblyFile)
        => _configs.TryGetValue(assemblyFile, out var cfg) ? cfg : GetOrCreateConfig(assemblyFile);

    public void Dispose()
    {
        Stop();
        _loader.Dispose();
    }
}
