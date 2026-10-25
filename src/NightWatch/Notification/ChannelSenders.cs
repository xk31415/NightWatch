using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

        return FormatCommon(alert, status,
            $"<font color=\"comment\">时间：{alert.Time:yyyy-MM-dd HH:mm:ss}</font>");
    }

    public static string FormatDingTalk(AlertMessage alert)
    {
        var status = alert.IsWarning
            ? "**⚠️ 状态：异常告警**"
            : "**✅ 状态：正常（测试/恢复通知）**";

        return FormatCommon(alert, status, $"时间：{alert.Time:yyyy-MM-dd HH:mm:ss}");
    }

    private static string FormatCommon(AlertMessage alert, string status, string time) =>
        $"### [{NotificationConstants.Tag}] {(alert.IsWarning ? "告警通知" : "状态通知")}\n" +
        $"> **{Escape(alert.Title)}**\n\n" +
        $"{Escape(alert.Body)}\n\n" +
        // 状态与时间之间必须空行：钉钉遵循标准 markdown（单个换行折叠为空格），
        // 企业微信则宽松得多（单换行也断行），用空行两边才一致
        $"{status}\n\n" +
        time;

    /// <summary>转义可能破坏 markdown 结构的字符，保留换行。</summary>
    private static string Escape(string text) =>
        text.Replace("#", "\\#").Replace("*", "\\*").Replace("<", "＜").Replace(">", "＞");
}

/// <summary>
/// 企微/钉钉共用的进程级 HttpClient。
/// 每个通道各自 new HttpClient（或按次创建）会在长驻 Windows 服务里把套接字留在 TIME_WAIT，最终耗尽端口。
/// <see cref="SocketsHttpHandler.PooledConnectionLifetime"/> 让连接池定期换连接，避免 DNS 变更后一直打到旧地址。
/// 该客户端跟随进程存活，不要在单次发送后 Dispose。
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

    public string ChannelName { get; }

    public WeComSender(NotifyChannel channel)
    {
        ChannelName = string.IsNullOrWhiteSpace(channel.Name) ? "企业微信" : channel.Name;
        _webhookUrl = channel.WebhookUrl ?? throw new ArgumentException("wecom 通道缺少 WebhookUrl");
    }

    public async Task SendAsync(AlertMessage alert, CancellationToken ct = default)
    {
        var payload = new { msgtype = "markdown", markdown = new { content = MarkdownFormatter.FormatWeCom(alert) } };
        using var resp = await WebhookHttp.Client.PostAsJsonAsync(_webhookUrl, payload, JsonOpts, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode || WebhookResponse.IsError(body))
            throw new InvalidOperationException($"企业微信返回异常: {(int)resp.StatusCode} {body}");
    }
}

/// <summary>钉钉群机器人（markdown 消息，支持加签）。</summary>
public class DingTalkSender : IChannelSender
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _webhookUrl;
    private readonly string? _secret;

    public string ChannelName { get; }

    public DingTalkSender(NotifyChannel channel)
    {
        ChannelName = string.IsNullOrWhiteSpace(channel.Name) ? "钉钉" : channel.Name;
        _webhookUrl = channel.WebhookUrl ?? throw new ArgumentException("dingtalk 通道缺少 WebhookUrl");
        _secret = channel.Secret;
    }

    public async Task SendAsync(AlertMessage alert, CancellationToken ct = default)
    {
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
        using var resp = await WebhookHttp.Client.PostAsJsonAsync(url, payload, JsonOpts, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode || WebhookResponse.IsError(body))
            throw new InvalidOperationException($"钉钉返回异常: {(int)resp.StatusCode} {body}");
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
    public static bool IsError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("errcode", out var prop))
                return true;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var code))
                return code != 0;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
                return parsed != 0;
            return true;
        }
        catch (JsonException)
        {
            return true;
        }
    }
}
