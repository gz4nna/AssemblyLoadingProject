using System.Reflection;
using System.Diagnostics;

namespace AssemblyLoadingProject.Plugins.Abstractions;

/// <summary>
/// 旧式/控制台风格插件的适配器。
///
/// 有些业务程序原本是独立的控制台项目（如 <c>testprogram</c>，含 static <c>Main</c> 方法），
/// 并未实现 <see cref="IDataTransferService"/>。为了让这类 DLL 无需改写即可被宿主调度，
/// 本适配器在每次执行时通过反射调用其静态入口方法（默认 <c>Main</c>）。
///
/// 依赖（如 TransDataHelper）由宿主共享解析，插件目录无需重复携带。
/// </summary>
public sealed class LegacyEntryPointPlugin : IDataTransferService
{
    private readonly Type _entryClass;
    private readonly MethodInfo _entryMethod;

    public string Id { get; }
    public string DisplayName { get; }
    public string Version { get; }
    public string Description { get; }

    public LegacyEntryPointPlugin(Type entryClass, MethodInfo entryMethod)
    {
        _entryClass = entryClass;
        _entryMethod = entryMethod;
        Id = "legacy:" + entryClass.FullName;
        DisplayName = $"旧式入口: {entryClass.Name}.{entryMethod.Name}";
        Version = entryClass.Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        Description = $"以反射调用 {entryClass.FullName}.{entryMethod.Name} 方式执行的控制台风格插件。";
    }

    public void Initialize(PluginContext context)
    {
        context.Logger($"旧式入口插件已初始化：{_entryClass.FullName}.{_entryMethod.Name}", LogLevel.Info);
    }

    public async Task<TransferResult> ExecuteAsync(PluginContext context, CancellationToken cancellationToken)
    {
        context.Logger($"开始调用入口方法 {_entryMethod.Name} ...", LogLevel.Info);
        var sw = Stopwatch.StartNew();
        try
        {
            // 构造参数：若入口方法带 string[] args 参数则传入空数组，否则无参
            object?[] args = _entryMethod.GetParameters().Length > 0
                ? new object?[] { Array.Empty<string>() }
                : Array.Empty<object?>();

            object? result;
            if (_entryMethod.IsStatic)
            {
                // 支持 void / object / Task / Task<T>
                result = _entryMethod.Invoke(null, args);
            }
            else
            {
                var instance = Activator.CreateInstance(_entryClass);
                result = _entryMethod.Invoke(instance, args);
            }

            if (result is Task task)
            {
                await task.WaitAsync(cancellationToken);
            }
            else if (result is ValueTask vt)
            {
                await vt.AsTask().WaitAsync(cancellationToken);
            }

            sw.Stop();
            context.Logger($"入口方法执行完成，耗时 {sw.ElapsedMilliseconds}ms", LogLevel.Info);
            return TransferResult.Ok($"入口方法执行完成", elapsedMs: sw.ElapsedMilliseconds);
        }
        catch (TargetInvocationException tie)
        {
            sw.Stop();
            var inner = tie.InnerException ?? tie;
            context.Logger($"入口方法异常：{inner}", LogLevel.Error);
            return TransferResult.Fail(inner.Message, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            context.Logger($"执行异常：{ex}", LogLevel.Error);
            return TransferResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public void Dispose()
    {
    }
}
