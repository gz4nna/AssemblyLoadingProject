using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace AssemblyLoadingProject.Plugins;

/// <summary>
/// 失败告警服务：在插件执行失败时，通过邮件与企业微信推送通知运维。
///
/// 参考实现：
///  - 邮件：内网邮件服务（email_service.py）——POST JSON { Destination, Subject, TemplateID, TemplateData }；
///  - 企业微信：内网消息推送服务（AttendInfoPush）——POST JSON { touser, msgtype, textcard:{...} }。
///
/// 安全约定：发件地址、收件邮箱、OA 账号等敏感信息<b>不硬编码</b>，一律从
/// <see cref="AppSettingsStore"/>（SQLite）读取；未配置则不发或使用空默认。
/// 开关由 <c>Alert:EmailEnabled</c> / <c>Alert:WeChatEnabled</c> 控制。
/// </summary>
public sealed class AlertService
{
    private readonly AppSettingsStore _settings;
    private readonly ILogger<AlertService> _logger;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public AlertService(AppSettingsStore settings, ILogger<AlertService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>在插件执行失败时触发告警（邮件 + 企业微信，按配置开关）。</summary>
    public async Task SendFailureAlertAsync(string pluginName, string errorMessage, CancellationToken ct)
    {
        var summary = $"插件 {pluginName} 执行失败";
        try
        {
            var emailEnabled = _settings.Get("Alert:EmailEnabled", "false") == "true";
            var wechatEnabled = _settings.Get("Alert:WeChatEnabled", "false") == "true";

            if (emailEnabled)
                await SendEmailAsync(summary, errorMessage, ct);

            if (wechatEnabled)
                await SendWeChatAsync(summary, errorMessage, ct);
        }
        catch (Exception ex)
        {
            // 告警本身失败不应影响主流程，仅记录日志
            _logger.LogWarning(ex, "发送失败告警时出错（插件 {Plugin}）", pluginName);
        }
    }

    private async Task SendEmailAsync(string subject, string errorMessage, CancellationToken ct)
    {
        var api = _settings.Get("Alert:EmailApi");
        var to = _settings.Get("Alert:EmailTo");
        if (string.IsNullOrEmpty(api) || string.IsNullOrEmpty(to))
        {
            _logger.LogWarning("未配置邮件告警地址/收件人，跳过邮件通知");
            return;
        }

        // 参考 email_service.py：POST /send
        var payload = new
        {
            Destination = new[] { to },
            Subject = subject,
            TemplateID = 181112,
            TemplateData = $"{{\"wrong_level\":\"高\",\"wrong_type\":\"插件执行异常\",\"wrong_description\":\"{errorMessage}\"}}"
        };
        var resp = await _http.PostAsJsonAsync(api, payload, ct);
        if (!resp.IsSuccessStatusCode)
            _logger.LogWarning("邮件告警发送失败 HTTP {Code}", (int)resp.StatusCode);
        else
            _logger.LogInformation("邮件告警已发送到 {To}", to);
    }

    private async Task SendWeChatAsync(string title, string errorMessage, CancellationToken ct)
    {
        var api = _settings.Get("Alert:PushUrl");
        var oaAccount = _settings.Get("Alert:OaAccount");
        if (string.IsNullOrEmpty(api) || string.IsNullOrEmpty(oaAccount))
        {
            _logger.LogWarning("未配置企业微信推送地址/账号，跳过推送");
            return;
        }

        // 参考 AttendInfoPush：POST /api/messagepush
        var payload = new
        {
            touser = oaAccount,
            msgtype = "textcard",
            textcard = new
            {
                title = title,
                description = $"执行失败：{errorMessage}\n发生时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                url = _settings.Get("Alert:TargetUrl", "")
            }
        };
        var resp = await _http.PostAsJsonAsync(api, payload, ct);
        if (!resp.IsSuccessStatusCode)
            _logger.LogWarning("企业微信推送失败 HTTP {Code}", (int)resp.StatusCode);
        else
            _logger.LogInformation("企业微信告警已推送到 {Oa}", oaAccount);
    }
}
