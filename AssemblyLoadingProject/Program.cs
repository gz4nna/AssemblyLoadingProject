using System.Reflection;
using System.Runtime.Loader;
using AssemblyLoadingProject.Plugins;
using Microsoft.AspNetCore.StaticFiles;

namespace AssemblyLoadingProject
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // 插件宿主服务：单例，负责扫描/加载/调度/状态跟踪。
            // 数据目录（含插件 DLL、SQLite 配置库、JSON 降级配置、日志）解析规则：
            //   1) 若 Plugins:Directory 为绝对路径（如 Docker volume 挂载点 /data），直接使用；
            //   2) 否则视为相对路径，拼接到应用基础目录下。
            // 部署在 Docker 时，可通过环境变量 Plugins__Directory（映射 Plugins:Directory）指向宿主 volume。
            var pluginsDir = builder.Configuration["Plugins:Directory"] ?? "Plugins";
            var pluginsPath = Path.IsPathRooted(pluginsDir)
                ? Path.GetFullPath(pluginsDir)
                : Path.Combine(AppContext.BaseDirectory, pluginsDir);

            // 确保数据目录存在（Docker 首次挂载空 volume 时由容器负责创建）
            Directory.CreateDirectory(pluginsPath);

            // 让默认上下文能从独立的"共享依赖目录"加载 TransDataHelper.dll。
            // 配合发布时 ExcludeFromSingleFile=true，TransDataHelper 以独立 DLL 输出，
            // 不再内嵌进单文件宿主；改动依赖库时只需替换该 DLL，无需重发布整个宿主。
            // 共享目录默认放在插件数据目录下的 lib/（Docker 中即挂载卷 /data/lib，
            // 与 logs/、plugins.db 等同在宿主机 volume 下，便于直接替换 DLL），
            // 可通过环境变量 SHARED_LIB_DIR 覆盖。
            var sharedLibDir = Environment.GetEnvironmentVariable("SHARED_LIB_DIR")
                ?? Path.Combine(pluginsPath, "lib");
            if (!Directory.Exists(sharedLibDir))
                Directory.CreateDirectory(sharedLibDir);
            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                if (name.Name is null) return null;
                var candidate = Path.Combine(sharedLibDir, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };

            builder.Services.AddSingleton(_ => new AppSettingsStore(pluginsPath));
            builder.Services.AddSingleton(sp =>
                new AlertService(sp.GetRequiredService<AppSettingsStore>(), sp.GetRequiredService<ILogger<AlertService>>()));
            builder.Services.AddSingleton(sp =>
                new PluginHostService(
                    sp.GetRequiredService<ILogger<PluginHostService>>(),
                    pluginsPath,
                    sp.GetRequiredService<AppSettingsStore>(),
                    sp.GetRequiredService<AlertService>()));
            builder.Services.AddHostedService<PluginHostedService>();

            var app = builder.Build();

            // 允许读取静态文件（wwwroot 下放置纯 HTML 页面）。
            // 关闭静态资源缓存（no-cache）：开发/迭代期间确保浏览器总是拿到最新页面，
            // 避免因浏览器缓存导致看到旧样式/旧功能。
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = ctx =>
                {
                    ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                    ctx.Context.Response.Headers["Pragma"] = "no-cache";
                }
            });
            app.UseDefaultFiles();

            // 后端 API：插件管理（纯 JSON，供前端 fetch 调用）
            MapApi(app, pluginsPath);

            // 未匹配到静态文件的请求回退到首页
            app.MapFallbackToFile("index.html");

            app.Run();
        }

        private static void MapApi(WebApplication app, string pluginsPath)
        {
            var plugins = app.Services.GetRequiredService<PluginHostService>();

            // 扫描并返回 DLL 列表（含状态）
            app.MapGet("/api/plugins", () =>
            {
                plugins.ScanPlugins();
                return Results.Ok(plugins.GetAllStates());
            });

            // 返回单个插件状态
            app.MapGet("/api/plugins/{file}/state", (string file) =>
            {
                var state = plugins.GetAllStates().FirstOrDefault(s =>
                    string.Equals(s.AssemblyFile, file, StringComparison.OrdinalIgnoreCase));
                return state == null ? Results.NotFound() : Results.Ok(state);
            });

            // 读取某插件的持久化历史日志（缺省最近 100 行）
            app.MapGet("/api/plugins/{file}/history", (string file, int lines = 100) =>
                Results.Ok(new { assemblyFile = file, lines = plugins.ReadLogHistory(file, lines) }));

            // 获取插件配置（含参数）
            app.MapGet("/api/plugins/{file}/config", (string file) =>
            {
                var cfg = plugins.GetConfig(file);
                return Results.Ok(cfg);
            });

            // 更新插件的显示名称/描述覆盖
            app.MapPost("/api/plugins/{file}/display", (string file, DisplayRequest req) =>
            {
                plugins.UpdateDisplay(file, req.DisplayName, req.Description);
                return Results.Ok(new { ok = true, message = "显示信息已更新" });
            });

            // 保存配置并启动/停止
            app.MapPost("/api/plugins/{file}/config", (string file, PluginConfig config) =>
            {
                config.AssemblyFile = file;

                // 校验调度配置，无效则拒绝保存并给出原因
                if (config.Schedule != null && !ScheduleEvaluator.Validate(config.Schedule).Ok)
                {
                    return Results.BadRequest(new { ok = false, message = ScheduleEvaluator.Validate(config.Schedule).Message });
                }

                plugins.UpdateConfig(file, config);
                return Results.Ok(new { ok = true, message = "配置已保存并应用" });
            });

            // 加载插件（不执行）
            app.MapPost("/api/plugins/{file}/load", (string file) =>
            {
                var ok = plugins.LoadAndStart(file, plugins.GetConfig(file));
                return ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { ok = false, message = "插件加载失败，请查看日志" });
            });

            // 立即执行一次
            app.MapPost("/api/plugins/{file}/run", async (string file) =>
            {
                var result = await plugins.RunOnceAsync(file);
                return result == null
                    ? Results.BadRequest(new { ok = false, message = "插件未加载或执行失败" })
                    : Results.Ok(new { ok = result.Success, message = result.Message, elapsedMs = result.ElapsedMilliseconds, rows = result.RowsAffected });
            });

            // 卸载插件
            app.MapPost("/api/plugins/{file}/unload", (string file) =>
            {
                plugins.UnloadPlugin(file);
                return Results.Ok(new { ok = true, message = "已卸载" });
            });

            // 启用/停用（快捷开关）
            app.MapPost("/api/plugins/{file}/enable", (string file, EnableRequest req) =>
            {
                var cfg = plugins.GetConfig(file);
                cfg.Enabled = req.Enabled;
                plugins.UpdateConfig(file, cfg);
                return Results.Ok(new { ok = true, enabled = cfg.Enabled });
            });

            // ---- 全局告警设置（存于 SQLite，避免硬编码敏感信息） ----
            // 读取全部告警/通知设置
            app.MapGet("/api/settings", () => Results.Ok(plugins.GetAppSettings()));

            // 保存告警/通知设置（覆盖式写入）
            app.MapPost("/api/settings", (Dictionary<string, string> settings) =>
            {
                plugins.AppSettings.Set(settings);
                return Results.Ok(new { ok = true, message = "设置已保存" });
            });
        }

        public sealed class EnableRequest
        {
            public bool Enabled { get; set; }
        }

        public sealed class DisplayRequest
        {
            public string? DisplayName { get; set; }
            public string? Description { get; set; }
        }
    }
}
