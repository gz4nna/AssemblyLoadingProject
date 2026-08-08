using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssemblyLoadingProject.Plugins;

/// <summary>
/// 插件配置的 JSON 持久化存储。
/// 把每个 DLL 的 <see cref="PluginConfig"/>（启用状态、执行间隔、参数等）保存为 JSON 文件，
/// 宿主重启后自动恢复，无需重新在前端配置。
///
/// 流程：
///  1. 进入项目后照常扫描插件，为每个 DLL 生成默认配置；
///  2. 从 JSON 读取历史配置并覆盖默认值；
///  3. 配置中 Enabled=true 的插件在宿主启动时自动加载调度。
/// </summary>
public sealed class PluginConfigStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public PluginConfigStore(string pluginsDirectory)
    {
        // 配置文件放在插件目录中，便于查看与管理（扫描只匹配 *.dll，不会误当作插件）
        _filePath = Path.Combine(pluginsDirectory, "plugins.config.json");
    }

    /// <summary>配置文件完整路径。</summary>
    public string FilePath => _filePath;

    /// <summary>从 JSON 读取已持久化的配置（按 AssemblyFile 区分大小写不敏感）。</summary>
    public Dictionary<string, PluginConfig> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new Dictionary<string, PluginConfig>(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<PluginConfig>>(json, _options);
            if (list == null)
                return new Dictionary<string, PluginConfig>(StringComparer.OrdinalIgnoreCase);

            return list
                .Where(c => !string.IsNullOrEmpty(c.AssemblyFile))
                .ToDictionary(c => c.AssemblyFile!, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // 读取失败不阻断启动，返回空字典走默认配置
            return new Dictionary<string, PluginConfig>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>把当前全部配置写回 JSON（原子写入：先写临时文件再替换）。</summary>
    public void Save(IEnumerable<PluginConfig> configs)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(configs.ToList(), _options);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception)
        {
            // 写入失败不抛出，避免前端操作被阻断；可在此记录日志
        }
    }
}
