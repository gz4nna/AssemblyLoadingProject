using AssemblyLoadingProject.Plugins.Abstractions;

namespace SampleThrowPlugin;

/// <summary>
/// 测试用插件：不执行任何实际业务，直接抛出异常。
/// 用于验证"失败告警"功能——插件异常会被宿主统一捕获，
/// 并触发邮件/企业微信告警（按 /settings.html 中配置的通道）。
/// </summary>
public sealed class ThrowPlugin : IDataTransferService
{
    public string Id => "sample.throw";

    public string DisplayName => "测试：直接抛出异常";

    public string Version => "1.0.0";

    public string Description => "用于验证失败告警功能：执行时必然抛出异常。";

    public void Initialize(PluginContext context)
    {
        context.Logger("测试插件已初始化", LogLevel.Info);
    }

    public Task<TransferResult> ExecuteAsync(PluginContext context, CancellationToken cancellationToken)
    {
        context.Logger("即将抛出异常以测试告警", LogLevel.Warn);
        // 设计约定：插件内部不做异常处理，直接抛出，由宿主统一识别处理
        throw new InvalidOperationException("这是用于测试告警的刻意异常");
    }

    public void Dispose()
    {
    }
}
