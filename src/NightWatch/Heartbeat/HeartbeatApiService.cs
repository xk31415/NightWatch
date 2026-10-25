using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NightWatch.Heartbeat;

/// <summary>
/// 内嵌 HTTP 心跳接收服务（Kestrel）：
///   POST /heartbeat/{product}/{user}  上报心跳
///   GET /heartbeats                        查看所有心跳状态（JSON）
/// </summary>
public class HeartbeatApiService : IHostedService, IAsyncDisposable
{
    private readonly HeartbeatRegistry _registry;
    private readonly Models.HttpHeartbeatOptions _options;
    private readonly ILogger<HeartbeatApiService> _logger;
    private WebApplication? _app;

    public HeartbeatApiService(HeartbeatRegistry registry, Models.HttpHeartbeatOptions options,
        ILogger<HeartbeatApiService> logger)
    {
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("HTTP 心跳服务未启用");
            return;
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.Logging.ClearProviders(); // 外层宿主已配置 Serilog，避免重复输出
        var host = string.IsNullOrWhiteSpace(_options.Host) ? "localhost" : _options.Host;
        builder.WebHost.UseUrls($"http://{host}:{_options.Port}");

        var app = builder.Build();

        // 鉴权：配置了 Token 时，所有请求需携带 X-NightWatch-Token / Bearer / ?token= 之一
        var token = _options.Token;
        if (!string.IsNullOrWhiteSpace(token))
        {
            app.Use(async (ctx, next) =>
            {
                var provided =
                    ctx.Request.Headers["X-NightWatch-Token"].FirstOrDefault()
                    ?? GetBearerToken(ctx.Request.Headers.Authorization.FirstOrDefault())
                    ?? ctx.Request.Query["token"].FirstOrDefault();

                if (provided is null || !TokensEqual(provided, token))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "unauthorized" });
                    return;
                }
                await next();
            });
            _logger.LogInformation("HTTP 心跳鉴权已启用（Token 模式）");
        }

        // 心跳上报仅接受 POST，避免 GET 触发写操作 / 被预取和爬虫误触发
        app.MapPost("/heartbeat/{product}/{user}",
            (string product, string user, HttpContext ctx) =>
            {
                _registry.Touch(product, user, ctx.Connection.RemoteIpAddress?.ToString());
                return Results.Ok(new
                {
                    ok = true,
                    product,
                    user,
                    receivedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                });
            });

        app.MapGet("/heartbeats", () => Results.Json(_registry.Snapshot().ToDictionary(
            kv => kv.Key,
            kv => new
            {
                ageSeconds = Math.Round((DateTime.UtcNow - kv.Value.LastSeenUtc).TotalSeconds, 1),
                lastSeen = kv.Value.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                ip = kv.Value.LastIp,
                count = kv.Value.Count
            })));

        app.MapGet("/", () => Results.Text("NightWatch heartbeat endpoint is running."));

        _app = app;
        await app.StartAsync(ct);
        _logger.LogInformation("HTTP 心跳服务已启动，监听 http://{Host}:{Port}。上报地址: POST http://<host>:{Port}/heartbeat/{{product}}/{{user}}",
            host, _options.Port, _options.Port);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_app is not null)
            await _app.StopAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    private static string? GetBearerToken(string? header) =>
        header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;

    /// <summary>
    /// 恒定时间比较，避免时序侧信道逐字节猜 Token。
    /// 长度不同直接拒绝：<c>FixedTimeEquals</c> 要求两段等长，否则抛 <see cref="ArgumentException"/>，
    /// 错误令牌会从 401 变成 500。
    /// </summary>
    internal static bool TokensEqual(string provided, string expected)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(provided);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        if (a.Length != b.Length)
            return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}
