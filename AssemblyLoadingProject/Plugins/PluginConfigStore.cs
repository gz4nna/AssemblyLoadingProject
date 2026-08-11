using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssemblyLoadingProject.Plugins;

/// <summary>
/// 插件配置的持久化存储门面（facade）。
/// 主用 <see cref="PluginConfigSqliteStore"/>（SQLite），失败时自动降级到 JSON 文件
/// （<see cref="PluginConfigJsonStore"/>），确保配置始终可读写。
///
/// 流程：
///  1. 进入项目后照常扫描插件，为每个 DLL 生成默认配置；
///  2. 优先从 SQLite 读取历史配置并覆盖默认值（SQLite 不可用则读 JSON）；
///  3. 配置中 Enabled=true 的插件在宿主启动时自动加载调度。
/// </summary>
public sealed class PluginConfigStore
{
    private readonly PluginConfigSqliteStore _sqlite;
    private readonly PluginConfigJsonStore _json;
    private bool _useJsonFallback;

    public PluginConfigStore(string pluginsDirectory)
    {
        _sqlite = new PluginConfigSqliteStore(pluginsDirectory);
        _json = new PluginConfigJsonStore(pluginsDirectory);

        // 探测 SQLite 是否可用：尝试读写一次，失败则切换 JSON 降级
        try
        {
            _sqlite.Save(Array.Empty<PluginConfig>());
            _useJsonFallback = false;
        }
        catch
        {
            _useJsonFallback = true;
        }
    }

    /// <summary>当前是否处于 JSON 降级模式（便于日志/排查）。</summary>
    public bool IsJsonFallback => _useJsonFallback;

    /// <summary>SQLite 数据库路径。</summary>
    public string DbPath => _sqlite.DbPath;

    /// <summary>JSON 配置文件路径。</summary>
    public string JsonPath => _json.FilePath;

    /// <summary>读取已持久化的配置：优先 SQLite，失败回退 JSON。</summary>
    public Dictionary<string, PluginConfig> Load()
    {
        if (!_useJsonFallback)
        {
            var fromDb = _sqlite.Load();
            // SQLite 为空但 JSON 有数据：做一次数据迁移/兜底
            if (fromDb.Count == 0)
            {
                var fromJson = _json.Load();
                if (fromJson.Count > 0)
                {
                    _sqlite.Save(fromJson.Values);
                    return fromJson;
                }
            }
            return fromDb;
        }

        return _json.Load();
    }

    /// <summary>保存配置：优先 SQLite；若当前处于降级模式则写 JSON。</summary>
    public void Save(IEnumerable<PluginConfig> configs)
    {
        var list = configs.ToList();
        if (_useJsonFallback)
        {
            _json.Save(list);
            return;
        }

        try
        {
            _sqlite.Save(list);
        }
        catch
        {
            // SQLite 写入失败，自动降级到 JSON 并缓存状态
            _useJsonFallback = true;
            _json.Save(list);
        }
    }
}

/// <summary>
/// JSON 文件存储（降级方案）。保留原有 JSON 文件逻辑。
/// </summary>
public sealed class PluginConfigJsonStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public PluginConfigJsonStore(string pluginsDirectory)
    {
        _filePath = Path.Combine(pluginsDirectory, "plugins.config.json");
    }

    public string FilePath => _filePath;

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
            return new Dictionary<string, PluginConfig>(StringComparer.OrdinalIgnoreCase);
        }
    }

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
            // 写入失败不抛出，避免前端操作被阻断
        }
    }
}
