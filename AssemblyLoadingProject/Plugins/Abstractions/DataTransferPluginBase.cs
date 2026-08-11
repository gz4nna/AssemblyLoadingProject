using System.Data;
using System.Data.Common;
using TransDataHelper.Adapters;
using TransDataHelper.Config.Connection;

namespace AssemblyLoadingProject.Plugins.Abstractions;

/// <summary>
/// 数据传输插件的便捷基类。
/// 封装了基于 <see cref="TransDataHelper"/> 的多库读写通用流程：
///  1. 依据前端参数构造源/目标数据库适配器；
///  2. 从源库读取数据（ExecuteReader → DataTable）；
///  3. 分批写入目标库（BatchInsert）。
///
/// 真实业务插件可直接继承本类，只重写 <see cref="BuildSourceSql"/> 与
/// <see cref="GetTargetColumns"/> 等，避免重复编写跨库样板代码。
/// 依赖的 TransDataHelper 由宿主共享提供，插件无需重复携带。
/// </summary>
public abstract class DataTransferPluginBase : IDataTransferService
{
    protected PluginContext? Context;
    protected DatabaseAdapter? SourceAdapter;
    protected DatabaseAdapter? TargetAdapter;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Version { get; }
    public abstract string Description { get; }

    /// <summary>源库类型标识：MySql / SqlServer / Oracle / Sybase / Sqlite。</summary>
    protected virtual string SourceDbType => "Sqlite";
    /// <summary>目标库类型标识：MySql / SqlServer / Sybase / Sqlite。注意 Oracle 禁止写入。</summary>
    protected virtual string TargetDbType => "Sqlite";

    public virtual void Initialize(PluginContext context)
    {
        Context = context;
        SourceAdapter = CreateAdapter(SourceDbType, context.Parameters, "Source");
        TargetAdapter = CreateAdapter(TargetDbType, context.Parameters, "Target");
        Context.Logger($"数据源({SourceDbType})与目标({TargetDbType})适配器已创建", LogLevel.Info);
    }

    /// <summary>构造源库查询 SQL（业务插件必须实现）。</summary>
    protected abstract string BuildSourceSql();

    /// <summary>源库查询需要绑定的参数（可选）。</summary>
    protected virtual DbParameter[] BuildSourceParameters() => Array.Empty<DbParameter>();

    /// <summary>目标表名（业务插件必须实现）。</summary>
    protected abstract string GetTargetTable();

    /// <summary>目标列清单（逗号分隔，顺序须与值一致）。</summary>
    protected virtual string GetTargetColumns() => string.Empty;

    /// <summary>从 DataRow 生成目标表一行（含列名）的 VALUES 片段。</summary>
    protected virtual string BuildRowSql(DataRow row)
    {
        var cols = GetTargetColumns();
        var colNames = string.IsNullOrEmpty(cols)
            ? string.Join(",", row.Table.Columns.Cast<DataColumn>().Select(c => c.ColumnName))
            : cols;

        var values = row.Table.Columns.Cast<DataColumn>()
            .Select(c => SqlizeValue(row[c.ColumnName]))
            .ToList();

        return $"INSERT INTO {GetTargetTable()} ({colNames}) VALUES ({string.Join(",", values)});";
    }

    public virtual async Task<TransferResult> ExecuteAsync(PluginContext context, CancellationToken cancellationToken)
    {
        Context = context;
        if (SourceAdapter == null || TargetAdapter == null)
        {
            Initialize(context);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // 设计约定：插件内部原则上不做异常处理，任何异常/错误都直接抛出。
        // 由宿主 PluginHostService.ExecutePluginAsync 统一捕获：标记状态、记录历史与日志、可触发告警。
        var sourceSql = BuildSourceSql();
        context.Logger($"开始从源库读取: {sourceSql}", LogLevel.Info);

        // 1) 源库读取
        using var reader = SourceAdapter!.ExecuteReader(sourceSql, BuildSourceParameters());
        var dt = new DataTable();
        dt.Load(reader);

        if (dt.Rows.Count == 0)
        {
            sw.Stop();
            context.Logger("源库没有新数据，本轮结束", LogLevel.Info);
            return TransferResult.Ok("源库无新数据", 0, sw.ElapsedMilliseconds);
        }
        context.Logger($"源库读取到 {dt.Rows.Count} 条记录", LogLevel.Info);

        // 2) 分批写入目标库（默认整批，可由参数控制）
        var batchSize = Math.Max(1, int.Parse(context.Parameters.GetValueOrDefault("BatchSize", "500")));
        var total = 0;
        for (int start = 0; start < dt.Rows.Count; start += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = Math.Min(start + batchSize, dt.Rows.Count);
            var sqls = new List<string>();
            for (int i = start; i < end; i++)
            {
                sqls.Add(BuildRowSql(dt.Rows[i]));
            }
            WriteBatch(sqls);
            total += end - start;
            context.Logger($"已写入批次 {start / batchSize + 1}（{end - start} 条，累计 {total}）", LogLevel.Info);
        }

        sw.Stop();
        context.Logger($"同步完成，共写入 {total} 条，耗时 {sw.ElapsedMilliseconds}ms", LogLevel.Info);
        return TransferResult.Ok($"成功写入 {total} 条", total, sw.ElapsedMilliseconds);
    }

    /// <summary>把一组 INSERT SQL 写入目标库（默认逐条；Sybase 可用事务批量）。</summary>
    protected virtual void WriteBatch(List<string> sqls)
    {
        foreach (var sql in sqls)
        {
            TargetAdapter!.ExecuteNonQuery(sql);
        }
    }

    public virtual void Dispose()
    {
        SourceAdapter?.Dispose();
        TargetAdapter?.Dispose();
        SourceAdapter = null;
        TargetAdapter = null;
    }

    /// <summary>
    /// 根据 dbType 与参数前缀创建 TransDataHelper 适配器。
    /// 前缀用于区分源/目标连接参数：源用 "Source"（如 SourceHost），目标用 "Target"。
    /// </summary>
    protected static DatabaseAdapter CreateAdapter(string dbType, IReadOnlyDictionary<string, string> parameters, string prefix)
    {
        // 按前缀拼接参数键并从字典取值；键不存在时使用给定默认值
        string GetParamValue(string key, string defaultValue) =>
            parameters.GetValueOrDefault($"{prefix}{key}", defaultValue);

        switch (dbType.ToLowerInvariant())
        {
            case "mysql":
                return new MySqlAdapter(new MySqlConnectionConfig
                {
                    DataSource = GetParamValue("Host", "127.0.0.1"),
                    Port = GetParamValue("Port", "3306"),
                    Database = GetParamValue("Database", ""),
                    User = GetParamValue("User", ""),
                    Password = GetParamValue("Password", ""),
                    Charset = GetParamValue("Charset", "utf8mb4"),
                });
            case "sqlserver":
                return new SqlServerAdapter(new SqlServerConnectionConfig
                {
                    DataSource = GetParamValue("Host", "127.0.0.1"),
                    Port = GetParamValue("Port", "1433"),
                    Database = GetParamValue("Database", ""),
                    User = GetParamValue("User", ""),
                    Password = GetParamValue("Password", ""),
                });
            case "oracle":
                return new OracleAdapter(new OracleConnectionConfig
                {
                    DataSource = GetParamValue("Host", "127.0.0.1"),
                    Port = GetParamValue("Port", "1521"),
                    Database = GetParamValue("Database", ""),
                    User = GetParamValue("User", ""),
                    Password = GetParamValue("Password", ""),
                    ServiceName = GetParamValue("ServiceName", GetParamValue("Database", "")),
                });
            case "sybase":
                return new SybaseAdapter(new SybaseConnectionConfig
                {
                    DataSource = GetParamValue("Host", "127.0.0.1"),
                    Port = GetParamValue("Port", "5000"),
                    Database = GetParamValue("Database", ""),
                    User = GetParamValue("User", ""),
                    Password = GetParamValue("Password", ""),
                });
            case "sqlite":
            default:
                return new SqliteAdapter(new SqliteConnectionConfig
                {
                    DataSource = GetParamValue("FilePath", "source.db"),
                });
        }
    }

    /// <summary>把 CLR 值转成 SQL 文本（空/数字/日期/字符串）。</summary>
    protected static string SqlizeValue(object? value)
    {
        if (value == null || value == DBNull.Value)
            return "NULL";

        if (value is bool b) return b ? "1" : "0";
        if (value is byte[] bytes) return "0x" + BitConverter.ToString(bytes).Replace("-", "");
        if (value is DateTime dt) return $"'{dt:yyyy-MM-dd HH:mm:ss}'";
        if (value is Guid g) return $"'{g}'";
        if (value is decimal || value is double || value is float ||
            value is sbyte || value is byte || value is short || value is ushort ||
            value is int || value is uint || value is long || value is ulong)
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;

        // 字符串：转义单引号
        var s = value.ToString()!.Replace("'", "''");
        return $"'{s}'";
    }
}
