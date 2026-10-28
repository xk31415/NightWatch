using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightWatch.Models;

namespace NightWatch.Notification;

/// <summary>单个通知通道的发送器。</summary>
public interface IChannelSender
{
    string ChannelName { get; }
    Task SendAsync(AlertMessage alert, CancellationToken ct = default);
}

/// <summary>markdown 消息排版；企业微信的颜色标签是其私有扩展，钉钉使用纯 markdown。</summary>
internal static class MarkdownFormatter
{
    public static string FormatWeCom(AlertMessage alert)
    {
        var status = alert.IsWarning
            ? "<font color=\"warning\">⚠️ 状态：异常告警</font>"
            : "<font color=\"info\">✅ 状态：正常（测试/恢复通知）</font>";

        var headline = $"**[{NotificationConstants.Tag}] {(alert.IsWarning ? "告警通知" : "状态通知")}**";
        return MarkdownLimit.Truncate(
            FormatCommon(alert, status,
                $"<font color=\"comment\">时间：{alert.Time:yyyy-MM-dd HH:mm:ss}</font>", headline),
            MarkdownLimit.WebhookMaxBytes);
    }

    public static string FormatDingTalk(AlertMessage alert)
    {
        var status = alert.IsWarning
            ? "**⚠️ 状态：异常告警**"
            : "**✅ 状态：正常（测试/恢复通知）**";

        // 钉钉 PC 端 ### 过大、纯加粗在手机端又不够醒目：四级标题折中。
        var headline = $"#### [{NotificationConstants.Tag}] {(alert.IsWarning ? "告警通知" : "状态通知")}";
        // 钉钉 markdown 上限比企微宽，但按同值保守截断，避免换通道后行为不一致。
        return MarkdownLimit.Truncate(
            FormatCommon(alert, status, $"时间：{alert.Time:yyyy-MM-dd HH:mm:ss}", headline),
            MarkdownLimit.WebhookMaxBytes);
    }

    private static string FormatCommon(AlertMessage alert, string status, string time, string headline)
    {
        // 状态与时间之间必须空行：钉钉遵循标准 markdown（单个换行折叠为空格），
        // 企业微信则宽松得多（单换行也断行），用空行两边才一致。
        return $"{headline}\n\n" +
               FormatHeaderBlock(alert) +
               $"{Escape(alert.Body)}\n\n" +
               $"{status}\n\n" +
               time;
    }

    /// <summary>
    /// 顶部信息块：主机、IP、告警项放在**同一个**引用块里（一个连续竖条，字段之间用空引用行分段）。
    /// 拆成多个引用块会在字段之间留出大段空白，看起来像一条条独立消息；
    /// 而字段之间只用单个换行又会被钉钉折叠成空格（渲染成「主机：xxxIP：yyy」）。
    /// </summary>
    private static string FormatHeaderBlock(AlertMessage alert)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(alert.Host))
            lines.Add($"> **主机：{Escape(alert.Host)}**");
        if (!string.IsNullOrWhiteSpace(alert.HostIp))
            lines.Add($"> **IP：{Escape(alert.HostIp)}**");
        lines.Add($"> **{Escape(alert.Title)}**");

        return string.Join("\n>\n", lines) + "\n\n";
    }

    /// <summary>转义可能破坏 markdown 结构的字符，保留换行。</summary>
    private static string Escape(string text) =>
        text.Replace("#", "\\#").Replace("*", "\\*").Replace("<", "＜").Replace(">", "＞");
}

/// <summary>
/// 企微/钉钉共用的进程级 HttpClient。
/// 每个通道各自 new HttpClient（或按次创建）会在长驻 Windows 服务里把套接字留在 TIME_WAIT，最终耗尽端口。
/// <see cref="SocketsHttpHandler.PooledConnectionLifetime"/> 让连接池定期换连接，避免 DNS 变更后一直打到旧地址。
/// 该客户端跟随进程存活，不要在单次发送后 Dispose。单次尝试的超时由 <see cref="WebhookRetry"/> 的
/// 取消令牌控制（5s），这里的 10s 只是兜底。
/// </summary>
internal static class WebhookHttp
{
    public static HttpClient Client { get; } = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
}

/// <summary>企业微信群机器人（markdown 消息）。</summary>
public class WeComSender : IChannelSender
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _webhookUrl;
    private readonly ILogger _logger;

    public string ChannelName { get; }

    public WeComSender(NotifyChannel channel, ILogger? logger = null)
    {
        ChannelName = string.IsNullOrWhiteSpace(channel.Name) ? "企业微信" : channel.Name;
        _webhookUrl = channel.WebhookUrl ?? throw new ArgumentException("wecom 通道缺少 WebhookUrl");
        _logger = logger ?? NullLogger.Instance;
    }

    public Task SendAsync(AlertMessage alert, CancellationToken ct = default)
    {
        var payload = new { msgtype = "markdown", markdown = new { content = MarkdownFormatter.FormatWeCom(alert) } };
        return WebhookRetry.ExecuteAsync(ChannelName, token => PostAsync(payload, token), _logger, ct);
    }

    private async Task PostAsync(object payload, CancellationToken ct)
    {
        using var resp = await WebhookHttp.Client.PostAsJsonAsync(_webhookUrl, payload, JsonOpts, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        WebhookResponse.EnsureSuccess("企业微信", resp.StatusCode, body);
    }
}

/// <summary>钉钉群机器人（markdown 消息，支持加签）。</summary>
public class DingTalkSender : IChannelSender
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _webhookUrl;
    private readonly string? _secret;
    private readonly ILogger _logger;

    public string ChannelName { get; }

    public DingTalkSender(NotifyChannel channel, ILogger? logger = null)
    {
        ChannelName = string.IsNullOrWhiteSpace(channel.Name) ? "钉钉" : channel.Name;
        _webhookUrl = channel.WebhookUrl ?? throw new ArgumentException("dingtalk 通道缺少 WebhookUrl");
        _secret = channel.Secret;
        _logger = logger ?? NullLogger.Instance;
    }

    public Task SendAsync(AlertMessage alert, CancellationToken ct = default)
    {
        // 签名 URL 在重试间复用：钉钉 timestamp/sign 的有效期是小时级，15s 预算内无需重算。
        var url = _webhookUrl;
        if (!string.IsNullOrWhiteSpace(_secret))
        {
            var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var sign = Sign(timestamp, _secret);
            var sep = url.Contains('?') ? "&" : "?";
            url = $"{url}{sep}timestamp={timestamp}&sign={Uri.EscapeDataString(sign)}";
        }

        var payload = new
        {
            msgtype = "markdown",
            markdown = new
            {
                title = $"[{NotificationConstants.Tag}] {(alert.IsWarning ? "告警" : "通知")}",
                text = MarkdownFormatter.FormatDingTalk(alert)
            }
        };

        return WebhookRetry.ExecuteAsync(ChannelName, token => PostAsync(url, payload, token), _logger, ct);
    }

    private async Task PostAsync(string url, object payload, CancellationToken ct)
    {
        using var resp = await WebhookHttp.Client.PostAsJsonAsync(url, payload, JsonOpts, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        WebhookResponse.EnsureSuccess("钉钉", resp.StatusCode, body);
    }

    /// <summary>钉钉加签：HMAC-SHA256(secret, "{timestamp}\n{secret}") 后 Base64。</summary>
    private static string Sign(long timestamp, string secret)
    {
        var stringToSign = $"{timestamp}\n{secret}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        return Convert.ToBase64String(hash);
    }
}

/// <summary>企微/钉钉 webhook 响应：按 JSON 的 errcode 判断，解析失败或字段缺失视为失败。</summary>
public static class WebhookResponse
{
    /// <summary>已知的限流 / 临时错误码：企微 45009（api freq out of limit）、钉钉 130101（发送过快）。</summary>
    private static readonly int[] RetryableErrCodes = [45009, 130101];

    public static bool IsError(string body) => !TryParse(body, out var errCode, out _) || errCode != 0;

    /// <summary>是否有可重试的限流错误码。</summary>
    public static bool IsRetryableErrcode(int errCode) => Array.IndexOf(RetryableErrCodes, errCode) >= 0;

    /// <summary>
    /// 解析 webhook 响应体：只有带整数 errcode 的合法 JSON 才返回 true。
    /// 解析失败（HTML 登录页、代理错误页等）按失败处理。
    /// </summary>
    public static bool TryParse(string body, out int errCode, out string errMsg)
    {
        errCode = 0;
        errMsg = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("errcode", out var prop))
                return false;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out errCode))
            {
                errMsg = ReadErrMsg(doc.RootElement);
                return true;
            }
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out errCode))
            {
                errMsg = ReadErrMsg(doc.RootElement);
                return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ReadErrMsg(JsonElement root) =>
        root.TryGetProperty("errmsg", out var msg) && msg.ValueKind == JsonValueKind.String
            ? msg.GetString() ?? ""
            : "";

    /// <summary>
    /// 按 HTTP 状态与 errcode 分类抛错：
    /// 5xx / 408 / 429 / 425 与限流 errcode 抛 <see cref="TransientNotificationException"/>（可重试）；
    /// 其余 4xx、业务错误、响应体不可解析都抛不可重试的 <see cref="InvalidOperationException"/>。
    /// </summary>
    /// <remarks>不在异常消息里拼接原始响应体，避免把 webhook key 之类的敏感 URL 带进日志。</remarks>
    public static void EnsureSuccess(string channel, HttpStatusCode statusCode, string body)
    {
        var code = (int)statusCode;
        if (code >= 500 || code is 408 or 425 or 429)
            throw new TransientNotificationException($"{channel} 临时失败: HTTP {code}");

        if (code is < 200 or >= 300)
            throw new InvalidOperationException($"{channel} 返回异常: HTTP {code}");

        if (!TryParse(body, out var errCode, out var errMsg))
            throw new InvalidOperationException($"{channel} 返回非预期响应: HTTP {code}（解析不到 errcode）");

        if (errCode == 0)
            return;

        var detail = string.IsNullOrWhiteSpace(errMsg) ? "" : $" {errMsg}";
        if (IsRetryableErrcode(errCode))
            throw new TransientNotificationException($"{channel} 限流/临时错误: errcode {errCode}{detail}");

        throw new InvalidOperationException($"{channel} 返回业务错误: errcode {errCode}{detail}");
    }
}
