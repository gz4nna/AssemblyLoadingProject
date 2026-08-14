using Microsoft.Data.Sqlite;

namespace AssemblyLoadingProject.Plugins;

/// <summary>
/// 全局默认连接参数存储（键值对，存于 SQLite 的 DefaultConnectionParams 表）。
/// 用途：把各插件 Initialize 里硬编码的默认连接参数（host/user/pwd/port 等）
/// 收敛到这里，前端可在"默认参数"区配置，插件不指定时由宿主合并注入兜底值。
///
/// 安全约定：默认连接参数（含密码）不再硬编码到插件源码/仓库，
/// 统一存于 SQLite（宿主 volume 下 plugins.db），并可通过前端在线维护。
/// </summary>
public sealed class DefaultParamStore
{
    private readonly string _dbPath;

    public DefaultParamStore(string pluginsDirectory)
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
                CREATE TABLE IF NOT EXISTS DefaultConnectionParams (
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

    /// <summary>读取全部默认参数。</summary>
    public Dictionary<string, string> LoadAll()
    {
        var result = new Dictionary<string, string>();
        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Key, Value FROM DefaultConnectionParams;";
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

    /// <summary>读取单个默认参数，未配置返回默认值。</summary>
    public string Get(string key, string? defaultValue = null)
        => LoadAll().TryGetValue(key, out var v) ? v : (defaultValue ?? string.Empty);

    /// <summary>覆盖式写入全部默认参数（保留原有、合并新增）。</summary>
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
                    INSERT INTO DefaultConnectionParams (Key, Value) VALUES ($k, $v)
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
