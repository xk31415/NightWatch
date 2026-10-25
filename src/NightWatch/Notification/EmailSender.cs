using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using NightWatch.Models;
using System.Text;

namespace NightWatch.Notification;

/// <summary>SMTP 邮件通知通道（MailKit），HTML 格式正文 + 纯文本兜底。</summary>
public class EmailSender : IChannelSender
{
    private readonly NotifyChannel _channel;
    private readonly ILogger _logger;

    public string ChannelName { get; }

    public EmailSender(NotifyChannel channel, ILogger logger)
    {
        _channel = channel;
        _logger = logger;
        ChannelName = string.IsNullOrWhiteSpace(channel.Name) ? "邮件" : channel.Name;
    }

    public async Task SendAsync(AlertMessage alert, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_channel.SmtpHost))
            throw new InvalidOperationException("email 通道未配置 SmtpHost");
        if (_channel.To.Count == 0)
            throw new InvalidOperationException("email 通道未配置收件人 To");

        var from = _channel.From ?? _channel.Username;
        if (string.IsNullOrWhiteSpace(from))
            throw new InvalidOperationException("email 通道未配置发件人 From 或 Username");

        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(from));
        foreach (var to in _channel.To)
            mime.To.Add(MailboxAddress.Parse(to));
        mime.Subject = $"[{NotificationConstants.Tag}] {(alert.IsWarning ? "⚠️ 告警" : "通知")} - {alert.Title}";
        mime.Body = new MultipartAlternative
        {
            { new TextPart("plain") { Text = BuildPlainText(alert) } },
            { new TextPart("html") { Text = BuildHtml(alert) } }
        };

        using var client = new SmtpClient { Timeout = 15_000 };
        await client.ConnectAsync(_channel.SmtpHost, _channel.SmtpPort,
            _channel.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(_channel.Username))
                await client.AuthenticateAsync(_channel.Username, _channel.Password ?? "", ct);

            await client.SendAsync(mime, ct);
        }
        finally
        {
            try
            {
                await client.DisconnectAsync(true, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SMTP 断开连接失败（已忽略，以免掩盖发送错误）");
            }
        }

        _logger.LogInformation("告警邮件已发送至 {To}", string.Join(", ", _channel.To));
    }

    private static string BuildPlainText(AlertMessage alert) => new StringBuilder()
        .AppendLine($"[{NotificationConstants.Tag}] {(alert.IsWarning ? "异常告警" : "通知")}")
        .AppendLine()
        .AppendLine($"告警项: {alert.Title}")
        .AppendLine($"详情: {alert.Body}")
        .AppendLine($"状态: {(alert.IsWarning ? "异常" : "正常")}")
        .Append($"时间: {alert.Time:yyyy-MM-dd HH:mm:ss}")
        .ToString();

    private static string BuildHtml(AlertMessage alert)
    {
        var (statusColor, statusText, icon) = alert.IsWarning
            ? ("#d93026", "异常告警", "⚠️")
            : ("#1a7f37", "正常（测试/恢复通知）", "✅");

        return $"""
<!DOCTYPE html>
<html>
<body style="margin:0;padding:24px;background:#f5f6f7;font-family:'Microsoft YaHei',Arial,sans-serif;">
  <div style="max-width:640px;margin:0 auto;background:#ffffff;border-radius:8px;overflow:hidden;border:1px solid #e5e6eb;">
    <div style="background:{(alert.IsWarning ? "#fff3f0" : "#f0f9eb")};padding:16px 24px;border-bottom:1px solid #e5e6eb;">
      <span style="font-size:18px;font-weight:bold;color:#1d2129;">🛡️ [{NotificationConstants.Tag}] 看门狗{(alert.IsWarning ? "告警" : "状态")}通知</span>
    </div>
    <div style="padding:24px;">
      <table style="width:100%;border-collapse:collapse;font-size:14px;color:#1d2129;">
        <tr>
          <td style="width:90px;padding:10px 0;color:#86909c;vertical-align:top;">告警项</td>
          <td style="padding:10px 0;font-weight:bold;">{System.Net.WebUtility.HtmlEncode(alert.Title)}</td>
        </tr>
        <tr>
          <td style="padding:10px 0;color:#86909c;vertical-align:top;">详情</td>
          <td style="padding:10px 0;line-height:1.7;">{System.Net.WebUtility.HtmlEncode(alert.Body).Replace("\n", "<br/>")}</td>
        </tr>
        <tr>
          <td style="padding:10px 0;color:#86909c;">状态</td>
          <td style="padding:10px 0;"><span style="color:{statusColor};font-weight:bold;">{icon} {statusText}</span></td>
        </tr>
        <tr>
          <td style="padding:10px 0;color:#86909c;">时间</td>
          <td style="padding:10px 0;color:#4e5969;">{alert.Time:yyyy-MM-dd HH:mm:ss}</td>
        </tr>
      </table>
    </div>
    <div style="padding:12px 24px;background:#fafafa;border-top:1px solid #e5e6eb;font-size:12px;color:#86909c;">
      本邮件由守夜人（NightWatch）看门狗服务自动发送
    </div>
  </div>
</body>
</html>
""";
    }
}
