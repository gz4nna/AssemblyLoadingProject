using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using AssemblyLoadingProject.Plugins.Abstractions;
using Microsoft.Extensions.Logging;

namespace AssemblyLoadingProject.Plugins;

/// <summary>
/// 核心插件加载器：基于 <see cref="AssemblyLoadContext"/> 实现 DLL 的扫描、加载、
/// 解析实例、卸载（热插拔）等能力。
///
/// 设计要点：
/// 1. 每个插件 DLL 使用独立的 <see cref="AssemblyLoadContext"/>（Collectible = true），
///    卸载后允许从内存回收，实现真正的"热插拔"。
/// 2. 插件实现的 <see cref="IDataTransferService"/> 接口类型必须来自宿主（共享）程序集，
///    因此解析依赖时优先回退到默认上下文（Default），避免插件把契约接口也带入自身上下文。
/// 3. 扫描（发现）与加载分离：扫描只记录文件信息，不立即执行；由调度引擎在参数就绪后触发加载。
/// </summary>
public sealed class PluginAssemblyLoader : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _pluginsDirectory;
    private readonly ConcurrentDictionary<string, PluginLoadSlot> _slots = new(StringComparer.OrdinalIgnoreCase);

    public PluginAssemblyLoader(ILogger logger, string pluginsDirectory)
    {
        _logger = logger;
        _pluginsDirectory = pluginsDirectory;
        Directory.CreateDirectory(_pluginsDirectory);
    }

    /// <summary>插件根目录。</summary>
    public string PluginsDirectory => _pluginsDirectory;

    /// <summary>当前所有已发现的插件槽位。</summary>
    public IReadOnlyCollection<PluginLoadSlot> Slots => _slots.Values.ToArray();

    /// <summary>
    /// 扫描插件目录，发现新的 DLL，并检测已有 DLL 的文件变更。
    /// 扫描只登记文件信息，不加载、不执行。
    /// </summary>
    public void Scan()
    {
        if (!Directory.Exists(_pluginsDirectory))
            return;

        var dllFiles = Directory.GetFiles(_pluginsDirectory, "*.dll")
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 新增 / 更新
        foreach (var file in dllFiles)
        {
            var fullPath = Path.Combine(_pluginsDirectory, file!);
            var lastWrite = File.GetLastWriteTimeUtc(fullPath);

            if (_slots.TryGetValue(file!, out var slot))
            {
                // 文件有更新 → 需要重载
                if (slot.State.FileLastWriteTime.HasValue &&
                    slot.State.FileLastWriteTime.Value != lastWrite &&
                    slot.Instance != null)
                {
                    slot.NeedsReload = true;
                    slot.State.StatusMessage = "检测到文件更新，等待重载";
                }
                slot.State.FileLastWriteTime = lastWrite;
            }
            else
            {
                _slots.TryAdd(file!, new PluginLoadSlot(file!)
                {
                    State = new PluginRunState
                    {
                        AssemblyFile = file!,
                        Status = PluginStatus.Discovered,
                        StatusMessage = "已扫描到，待加载",
                        FileLastWriteTime = lastWrite,
                    }
                });
                _logger.LogInformation("发现新插件文件: {File}", file);
            }
        }

        // 移除已删除的文件（若已加载则卸载）
        foreach (var key in _slots.Keys)
        {
            if (!dllFiles.Contains(key))
            {
                if (_slots.TryRemove(key, out var slot))
                {
                    _logger.LogInformation("插件文件被移除: {File}", key);
                    // 交由调度引擎决定是否立即卸载；此处记录状态
                    slot.State.Status = PluginStatus.Unloaded;
                    slot.State.StatusMessage = "文件已被移除";
                }
            }
        }
    }

    /// <summary>
    /// 从 DLL 加载插件并创建 <see cref="IDataTransferService"/> 实例。
    /// 每个文件独立一个 AssemblyLoadContext。若已加载则先卸载。
    /// </summary>
    public IDataTransferService? Load(string assemblyFile)
    {
        if (!_slots.TryGetValue(assemblyFile, out var slot))
            throw new InvalidOperationException($"插件 {assemblyFile} 未在扫描结果中，请先扫描。");

        Unload(assemblyFile);

        var fullPath = Path.Combine(_pluginsDirectory, assemblyFile);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"插件文件不存在: {fullPath}");

        // 使用独立且可回收的 AssemblyLoadContext
        slot.LoadContext = new AssemblyLoadContext(
            name: $"Plugin::{assemblyFile}::{Guid.NewGuid():N}",
            isCollectible: true);

        // 解析插件内部依赖：
        //   1) 优先用宿主默认上下文（TransDataHelper 等共享依赖已由宿主从 lib/ 加载，
        //      可保证拿到最新版，避免被插件目录里的旧副本遮蔽）；
        //   2) 宿主解析不到（插件私有依赖）再回退插件目录。
        slot.LoadContext.Resolving += (ctx, name) =>
        {
            if (name.Name is not null)
            {
                try
                {
                    return AssemblyLoadContext.Default.LoadFromAssemblyName(name);
                }
                catch (FileNotFoundException) { /* 宿主无此程序集，走插件目录 */ }
            }

            var localPath = Path.Combine(_pluginsDirectory, name.Name + ".dll");
            if (File.Exists(localPath))
                return ctx.LoadFromAssemblyPath(localPath);

            return null;
        };

        try
        {
            var assembly = slot.LoadContext.LoadFromAssemblyPath(fullPath);
            // 契约接口来自宿主，使用 default context 的类型
            var contractType = typeof(IDataTransferService);

            var implType = assembly.GetTypes()
                .FirstOrDefault(t => !t.IsAbstract && !t.IsInterface && contractType.IsAssignableFrom(t));

            IDataTransferService? instance;
            if (implType is null)
            {
                // 未实现契约接口：尝试作为"旧式控制台入口"插件（含 static Main 的类）执行
                var legacy = TryCreateLegacyPlugin(assembly);
                if (legacy is null)
                {
                    slot.LoadContext.Unload();
                    slot.LoadContext = null;
                    slot.Instance = null;
                    slot.State.Status = PluginStatus.Faulted;
                    slot.State.StatusMessage = $"程序集中既未实现 IDataTransferService，也未找到可调用的静态入口方法";
                    _logger.LogWarning("插件 {File} 中未找到契约实现或静态入口", assemblyFile);
                    return null;
                }
                instance = legacy;
                _logger.LogInformation("插件 {File} 以旧式入口模式加载: {Name}", assemblyFile, legacy.DisplayName);
            }
            else
            {
                instance = (IDataTransferService)Activator.CreateInstance(implType)!;
            }

            slot.Instance = instance;
            slot.State.Id = instance.Id;
            slot.State.DisplayName = instance.DisplayName;
            slot.State.Version = instance.Version;
            slot.State.Description = instance.Description;
            slot.State.Status = PluginStatus.Loaded;
            slot.State.StatusMessage = "已加载，等待调度";
            slot.NeedsReload = false;

            _logger.LogInformation("插件已加载: {Name} v{Version} (from {File})", instance.DisplayName, instance.Version, assemblyFile);
            return instance;
        }
        catch (Exception ex)
        {
            slot.LoadContext?.Unload();
            slot.LoadContext = null;
            slot.Instance = null;
            slot.State.Status = PluginStatus.Faulted;
            slot.State.StatusMessage = $"加载失败: {ex.Message}";
            _logger.LogError(ex, "加载插件失败: {File}", assemblyFile);
            return null;
        }
    }

    /// <summary>
    /// 尝试把程序集作为"旧式控制台入口"插件包装：查找含静态 <c>Main</c> 的类。
    /// 找不到则返回 null。
    /// </summary>
    private static IDataTransferService? TryCreateLegacyPlugin(Assembly assembly)
    {
        // 优先找带 Main 的类型
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract) continue;
            var main = type.GetMethod("Main",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (main == null) continue;

            // Main 应无参或 string[] 参数
            var ps = main.GetParameters();
            if (ps.Length > 1) continue;
            if (ps.Length == 1 && ps[0].ParameterType != typeof(string[])) continue;

            return new LegacyEntryPointPlugin(type, main);
        }

        // 退而求其次：找任意公开无参静态方法（可由参数 EntryMethod 指定，此处取第一个）
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract) continue;
            var any = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.GetParameters().Length == 0 && m.ReturnType != typeof(void));
            if (any != null)
                return new LegacyEntryPointPlugin(type, any);
        }

        return null;
    }

    /// <summary>
    /// 卸载插件：调用实例的 Dispose、卸载 AssemblyLoadContext，并触发垃圾回收以便真正释放。
    /// </summary>
    public void Unload(string assemblyFile)
    {
        if (!_slots.TryGetValue(assemblyFile, out var slot))
            return;

        slot.Instance?.Dispose();
        slot.Instance = null;

        var ctx = slot.LoadContext;
        slot.LoadContext = null;

        if (ctx != null)
        {
            try
            {
                ctx.Unload();
                // 触发 GC 让卸载完成（配合 WeakReference 可验证）。
                // 生产环境建议异步延迟触发，避免在请求线程中阻塞。
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "卸载插件 {File} 时发生异常", assemblyFile);
            }
        }

        slot.State.Status = PluginStatus.Unloaded;
        slot.State.StatusMessage = "已卸载";
    }

    /// <summary>卸载全部插件并释放资源。</summary>
    public void Dispose()
    {
        foreach (var file in _slots.Keys)
            Unload(file);
    }
}

/// <summary>
/// 单个插件文件的加载槽位。保存其 AssemblyLoadContext、实例与运行时状态。
/// </summary>
public sealed class PluginLoadSlot
{
    public PluginLoadSlot(string assemblyFile) => AssemblyFile = assemblyFile;

    /// <summary>DLL 文件名。</summary>
    public string AssemblyFile { get; }

    /// <summary>独立的可回收加载上下文。</summary>
    public AssemblyLoadContext? LoadContext { get; set; }

    /// <summary>解析出的插件实例。</summary>
    public IDataTransferService? Instance { get; set; }

    /// <summary>运行时状态（用于 UI 展示）。</summary>
    public PluginRunState State { get; set; } = new();

    /// <summary>是否检测到文件更新需要重载。</summary>
    public bool NeedsReload { get; set; }
}
