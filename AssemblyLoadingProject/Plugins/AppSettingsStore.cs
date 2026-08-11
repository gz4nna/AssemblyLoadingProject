using Microsoft.Data.Sqlite;

namespace AssemblyLoadingProject.Plugins;

/// <summary>
/// 全局应用设置存储（键值对），用于存放邮件/推送等告警配置等敏感信息。
/// 主用 SQLite（<c>plugins.db</c> 中的 AppSettings 表），避免硬编码到代码/提交到仓库。
/// 未配置的项可用默认值兜底（默认值应是无敏感内容的占位/空）。
/// </summary>
public sealed class AppSettingsStore
{
    private readonly string _dbPath;

    public AppSettingsStore(string pluginsDirectory)
    {
        _dbPath = Path.Combine(pluginsDirectory, "plugins.db");
        EnsureSchema();
    }

    private string ConnectionString => $"Data Source={_dbPath}";

    private void EnsureSchema()
    {
        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS AppSettings (
                    Key   TEXT PRIMARY KEY,
                    Value TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // 初始化失败不抛出，读取时返回空/默认
        }
    }

    /// <summary>读取全部设置。</summary>
    public Dictionary<string, string> LoadAll()
    {
        var result = new Dictionary<string, string>();
        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Key, Value FROM AppSettings;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = reader.GetString(1);
        }
        catch
        {
            // 忽略读取失败
        }
        return result;
    }

    /// <summary>读取单个设置，未配置返回默认值。</summary>
    public string Get(string key, string? defaultValue = null)
        => LoadAll().TryGetValue(key, out var v) ? v : (defaultValue ?? string.Empty);

    /// <summary>写入（更新）多个设置。</summary>
    public void Set(IEnumerable<KeyValuePair<string, string>> settings)
    {
        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();
            foreach (var kv in settings)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO AppSettings (Key, Value) VALUES ($k, $v)
                    ON CONFLICT(Key) DO UPDATE SET Value = $v;
                    """;
                cmd.Parameters.AddWithValue("$k", kv.Key);
                cmd.Parameters.AddWithValue("$v", kv.Value);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch
        {
            // 写入失败不抛出
        }
    }
}
