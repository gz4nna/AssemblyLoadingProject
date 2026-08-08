using AssemblyLoadingProject.Plugins.Abstractions;
using LogLevel = AssemblyLoadingProject.Plugins.Abstractions.LogLevel;

namespace AssemblyLoadingProject.Plugins;

/// <summary>
/// 插件日志的落盘存储（轻量处理）。
///
/// 设计目标：长时间运行后，避免把所有日志一直放在内存/一次性推给前端。
/// 采用分级处理：
///  1. <b>近期日志</b>：保留在内存（<see cref="PluginRunState"/>），状态页直接展示最近一小段时间的内容；
///  2. <b>历史日志</b>：每次写盘追加到插件专属的日志文件（<c>Plugins/logs/&lt;assembly&gt;.log</c>），
///     长期信息持久化保存，文件超过上限自动轮转压缩；
///  3. <b>冗余汇总</b>：执行期仅记录少量关键日志，每次执行的成败/耗时/条数作为一条"执行历史"汇总，
///     而非把所有细节都保留。
/// </summary>
public sealed class PluginLogStore
{
    private readonly string _logDir;
    private readonly object _lock = new();
    private const long MaxFileBytes = 2 * 1024 * 1024; // 单文件 2MB，超限轮转

    public PluginLogStore(string pluginsDirectory)
    {
        _logDir = Path.Combine(pluginsDirectory, "logs");
        Directory.CreateDirectory(_logDir);
    }

    /// <summary>日志目录（便于前端展示路径）。</summary>
    public string LogDirectory => _logDir;

    /// <summary>把一条日志追加到插件专属日志文件（线程安全，自动轮转）。</summary>
    public void LogToFile(string assemblyFile, string message, LogLevel level, DateTimeOffset time)
    {
        var safeName = SanitizeFileName(assemblyFile);
        var path = Path.Combine(_logDir, safeName + ".log");
        var line = $"[{time:yyyy-MM-dd HH:mm:ss.fff}][{level}] {message}";

        lock (_lock)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    // 轮转：把当前文件改名为 .1，旧的丢弃（保留最新）
                    try { File.Move(path, path + ".1", overwrite: true); }
                    catch { }
                }
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // 写日志失败不影响业务
            }
        }
    }

    private static string SanitizeFileName(string name)
        => string.Concat(name.Split(Path.GetInvalidFileNameChars()));

    /// <summary>读取某插件的最近历史日志（供前端按需查看，缺省最近 100 行）。</summary>
    public List<string> ReadHistory(string assemblyFile, int maxLines = 100)
    {
        var safeName = SanitizeFileName(assemblyFile);
        var path = Path.Combine(_logDir, safeName + ".log");
        try
        {
            if (!File.Exists(path)) return new List<string>();
            var lines = File.ReadAllLines(path);
            var tail = lines.Length > maxLines ? lines.Skip(lines.Length - maxLines) : lines;
            return tail.ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}
