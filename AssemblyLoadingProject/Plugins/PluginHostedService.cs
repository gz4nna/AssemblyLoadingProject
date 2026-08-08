using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AssemblyLoadingProject.Plugins;

/// <summary>
/// 将 <see cref="PluginHostService"/> 包装为标准托管服务（IHostedService），
/// 由 ASP.NET Core 宿主统一管理其生命周期。
///
/// 采用 IHostedService 而非在 Program.Main 中手动解析并调用 Start/Stop，
/// 好处：
///  1. 避免在 app.Run() 之前手动解析服务（某些环境会导致解析阻塞）；
///  2. 宿主的 StartAsync/StopAsync 会在 Web 宿主启动/停止时自动触发，
///     生命周期正确，且异常由宿主统一处理。
/// </summary>
public sealed class PluginHostedService : IHostedService
{
    private readonly PluginHostService _hostService;
    private readonly ILogger<PluginHostedService> _logger;

    public PluginHostedService(PluginHostService hostService, ILogger<PluginHostedService> logger)
    {
        _hostService = hostService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("插件宿主托管服务启动");
        try
        {
            // 启动流程：
            //  1) 扫描插件并生成默认配置；
            //  2) 从 JSON 读取历史配置覆盖默认值；
            //  3) 配置中 Enabled=true 的插件自动加载并纳入调度；
            //  4) 启动后台调度循环。
            _hostService.LoadPersistedAndStart();
            _hostService.Start();
        }
        catch (Exception ex)
        {
            // 插件启动失败不应阻断整个 Web 宿主
            _logger.LogError(ex, "插件宿主启动失败，请检查插件目录");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("插件宿主托管服务停止");
        _hostService.Stop();
        return Task.CompletedTask;
    }
}
