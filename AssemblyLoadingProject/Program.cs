using AssemblyLoadingProject.Plugins;
using Microsoft.AspNetCore.StaticFiles;

namespace AssemblyLoadingProject
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // 插件宿主服务：单例，负责扫描/加载/调度/状态跟踪
            var pluginsDir = builder.Configuration["Plugins:Directory"] ?? "Plugins";
            var pluginsPath = Path.Combine(AppContext.BaseDirectory, pluginsDir);
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
