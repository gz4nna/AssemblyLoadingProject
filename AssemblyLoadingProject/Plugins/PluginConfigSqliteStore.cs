using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace AssemblyLoadingProject.Plugins;

/// <summary>
/// 插件配置的 SQLite 持久化存储（主用方案）。
/// 把每个 DLL 的 <see cref="PluginConfig"/> 序列化后存入 <c>plugins.db</c>。
///
/// 相比 JSON 文件，SQLite 支持更可靠的并发写入与事务，更适合长时间运行场景。
/// 若 SQLite 不可用（如环境异常），由 <see cref="PluginConfigStore"/> 回退到 JSON 存储。
/// </summary>
public sealed class PluginConfigSqliteStore
{
    private readonly string _dbPath;
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public PluginConfigSqliteStore(string pluginsDirectory)
    {
        _dbPath = Path.Combine(pluginsDirectory, "plugins.db");
        EnsureSchema();
    }

    /// <summary>数据库文件路径。</summary>
    public string DbPath => _dbPath;

    /// <summary>连接字符串。</summary>
    private string ConnectionString => $"Data Source={_dbPath}";

    private void EnsureSchema()
    {
        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS PluginConfigs (
                    AssemblyFile TEXT PRIMARY KEY,
                    ConfigJson   TEXT NOT NULL,
                    UpdatedAt    TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // SQLite 初始化失败不抛出，交由上层回退 JSON
        }
    }

    /// <summary>从 SQLite 读取全部已持久化的配置。</summary>
    public Dictionary<string, PluginConfig> Load()
    {
        var result = new Dictionary<string, PluginConfig>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ConfigJson FROM PluginConfigs;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var cfg = JsonSerializer.Deserialize<PluginConfig>(json, _options);
                if (cfg != null && !string.IsNullOrEmpty(cfg.AssemblyFile))
                    result[cfg.AssemblyFile] = cfg;
            }
        }
        catch
        {
            return new Dictionary<string, PluginConfig>(StringComparer.OrdinalIgnoreCase);
        }
        return result;
    }

    /// <summary>把全部配置写入 SQLite（逐条 upsert，事务提交）。</summary>
    public void Save(IEnumerable<PluginConfig> configs)
    {
        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();
            foreach (var cfg in configs)
            {
                if (string.IsNullOrEmpty(cfg.AssemblyFile)) continue;
                var json = JsonSerializer.Serialize(cfg, _options);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO PluginConfigs (AssemblyFile, ConfigJson, UpdatedAt)
                    VALUES ($file, $json, $time)
                    ON CONFLICT(AssemblyFile) DO UPDATE SET
                        ConfigJson = $json,
                        UpdatedAt  = $time;
                    """;
                cmd.Parameters.AddWithValue("$file", cfg.AssemblyFile);
                cmd.Parameters.AddWithValue("$json", json);
                cmd.Parameters.AddWithValue("$time", DateTimeOffset.Now.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch
        {
            // 写入失败不抛出，交由上层回退 JSON
        }
    }
}
