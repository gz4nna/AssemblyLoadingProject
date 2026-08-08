using System.Data;
using AssemblyLoadingProject.Plugins.Abstractions;
using TransDataHelper.Adapters;

namespace SampleSqlSyncPlugin;

/// <summary>
/// 示例数据传输插件：演示如何基于 TransDataHelper 实现跨库同步。
///
/// 默认采用 SQLite → SQLite 的本地可运行演示（无需部署数据库服务器即可测试热插拔）：
///  - 源库：参数 SourceFilePath 指定的 db 文件（首次运行自动建表并写入种子数据）；
///  - 目标库：参数 TargetFilePath 指定的 db 文件（自动建表）。
///
/// 若要在真实环境使用，只需把 SourceDbType / TargetDbType 改为 MySql/SqlServer/Oracle/Sybase，
/// 并在前端补充对应连接参数（SourceHost/SourceUser/SourcePassword 等，见 DataTransferPluginBase）。
/// </summary>
public sealed class SqlSyncDemoPlugin : DataTransferPluginBase
{
    private readonly object _seedLock = new();
    private bool _seeded;

    public override string Id => "sample.transdatahelper.sqlite2sqlite";

    public override string DisplayName => "示例：SQLite→SQLite 数据同步 (TransDataHelper)";

    public override string Version => "1.0.0";

    public override string Description => "基于 TransDataHelper 从源 SQLite 增量同步到目标 SQLite，演示跨库数据传输核心。";

    public override void Initialize(PluginContext context)
    {
        base.Initialize(context);

        // 首次初始化：确保源/目标库结构与种子数据存在（便于本地演示）
        lock (_seedLock)
        {
            if (!_seeded)
            {
                EnsureSourceReady();
                EnsureTargetReady();
                _seeded = true;
            }
        }
    }

    /// <summary>源库：SQLite（参数 SourceFilePath）。</summary>
    protected override string SourceDbType => "Sqlite";

    /// <summary>目标库：SQLite（参数 TargetFilePath）。</summary>
    protected override string TargetDbType => "Sqlite";

    protected override string BuildSourceSql()
        => "SELECT Id, Name, Amount, CreateTime, IsSynced FROM Orders WHERE IsSynced = 0";

    protected override string GetTargetTable() => "OrdersArchive";

    protected override string GetTargetColumns() => "Id, Name, Amount, CreateTime, SyncTime";

    protected override string BuildRowSql(DataRow row)
    {
        // 源表字段到目标表字段的映射（目标多一列 SyncTime = 当前时间）
        return $"INSERT INTO OrdersArchive (Id, Name, Amount, CreateTime, SyncTime) VALUES (" +
               $"{SqlizeValue(row["Id"])}, " +
               $"{SqlizeValue(row["Name"])}, " +
               $"{SqlizeValue(row["Amount"])}, " +
               $"{SqlizeValue(row["CreateTime"])}, " +
               $"'{DateTime.Now:yyyy-MM-dd HH:mm:ss}');";
    }

    /// <summary>覆盖基类写库逻辑：先写目标，再把源记录标记为已同步（实现增量）。</summary>
    protected override void WriteBatch(List<string> sqls)
    {
        // 1) 写入目标表
        foreach (var sql in sqls)
        {
            TargetAdapter!.ExecuteNonQuery(sql);
        }

        // 2) 标记源记录为已同步（增量）
        SourceAdapter!.ExecuteNonQuery("UPDATE Orders SET IsSynced = 1 WHERE IsSynced = 0");
        Context!.Logger("已将源记录标记为已同步", LogLevel.Info);
    }

    private void EnsureSourceReady()
    {
        var src = (SqliteAdapter)SourceAdapter!;
        src.ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS Orders (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Amount REAL DEFAULT 0,
                CreateTime TEXT,
                IsSynced INTEGER DEFAULT 0
            );");

        // 若源表为空则写入种子数据
        long cnt;
        using (var cmd = src.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM Orders";
            cnt = Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
        }

        if (cnt == 0)
        {
            for (int i = 1; i <= 3; i++)
            {
                src.ExecuteNonQuery(
                    $"INSERT INTO Orders (Name, Amount, CreateTime) VALUES ('订单{i}', {i * 100.5}, '{DateTime.Now.AddDays(-i):yyyy-MM-dd HH:mm:ss}');");
            }
            Context!.Logger($"源库已写入 3 条种子数据", LogLevel.Info);
        }
    }

    private void EnsureTargetReady()
    {
        var tgt = (SqliteAdapter)TargetAdapter!;
        tgt.ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS OrdersArchive (
                Id INTEGER PRIMARY KEY,
                Name TEXT,
                Amount REAL,
                CreateTime TEXT,
                SyncTime TEXT
            );");
    }

    public override void Dispose()
    {
        base.Dispose();
    }
}
