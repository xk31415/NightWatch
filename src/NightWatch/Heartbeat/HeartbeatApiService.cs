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
    /// <summary>上报路径中 product / user 单段的最大长度。</summary>
    internal const int MaxSegmentLength = 128;

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

    /// <summary>端点是否真正处于监听状态（未启用、鉴权配置非法、端口占用均为 false）。</summary>
    internal bool IsStarted => _app is not null;

    public async Task StartAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("HTTP 心跳服务未启用");
            return;
        }

        var host = string.IsNullOrWhiteSpace(_options.Host) ? "localhost" : _options.Host.Trim();

        // 对外监听又没有 Token = 任何人可写入/伪造心跳，且能无界灌键。
        // 这里直接拒绝启动（降级），而不是悄悄裸奔或让宿主启动失败。
        if (RequiresToken(host, _options.Token))
        {
            _logger.LogError(
                "HTTP 心跳服务拒绝启动：Host={Host} 会对外监听，但未配置 WatchDog:HttpHeartbeat:Token。" +
                "请配置随机长令牌，或把 Host 改回 localhost。接收端已降级关闭，其余监控继续运行", host);
            return;
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.Logging.ClearProviders(); // 外层宿主已配置 Serilog，避免重复输出
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
        var maxKeys = _options.MaxKeys <= 0 ? int.MaxValue : _options.MaxKeys;
        app.MapPost("/heartbeat/{product}/{user}",
            (string product, string user, HttpContext ctx) =>
            {
                if (!IsValidSegment(product) || !IsValidSegment(user))
                {
                    return Results.Json(
                        new { ok = false, error = $"product/user 必须非空且不超过 {MaxSegmentLength} 字符" },
                        statusCode: StatusCodes.Status400BadRequest);
                }

                product = product.Trim();
                user = user.Trim();
                var ip = ctx.Connection.RemoteIpAddress?.ToString();
                if (!_registry.Touch(product, user, ip, maxKeys))
                {
                    return Results.Json(
                        new { ok = false, error = "heartbeat registry is full" },
                        statusCode: StatusCodes.Status429TooManyRequests);
                }

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
        try
        {
            await app.StartAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 宿主在启动过程中被要求停止，按正常取消处理
            _app = null;
            await DisposeAppAsync(app);
            throw;
        }
        catch (Exception ex)
        {
            // HTTP 心跳接收端是可选项：端口被占用/绑定失败时不能抛出——否则宿主 StartAsync 失败，
            // 进程守护、服务守护等核心监控会一起停摆，而它们才是看门狗的本职。
            // 这里降级关闭接收端并记 ERR，其余托管服务继续运行。
            _logger.LogError(ex,
                "HTTP 心跳服务启动失败（监听 http://{Host}:{Port}），接收端已降级关闭，其余监控继续运行。排查端口占用后重启服务",
                host, _options.Port);
            _app = null;
            await DisposeAppAsync(app);
            return;
        }

        _logger.LogInformation("HTTP 心跳服务已启动，监听 http://{Host}:{Port}。上报地址: POST http://<host>:{Port}/heartbeat/{{product}}/{{user}}",
            host, _options.Port, _options.Port);
    }

    private static async Task DisposeAppAsync(WebApplication app)
    {
        try
        {
            await app.DisposeAsync();
        }
        catch
        {
            // 启动失败后的清理失败不应影响宿主
        }
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

    /// <summary>仅本机可访问的监听地址（localhost / IPv4 回环 / IPv6 回环）。</summary>
    internal static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.Ordinal)
        || host.Equals("::1", StringComparison.Ordinal)
        || host.Equals("[::1]", StringComparison.Ordinal);

    /// <summary>对外监听（0.0.0.0 / * / + / 具体网卡地址）时必须有 Token。</summary>
    internal static bool RequiresToken(string host, string? token) =>
        !IsLoopbackHost(host) && string.IsNullOrWhiteSpace(token);

    /// <summary>
    /// 校验 URL 路径段：非空、不超过 <see cref="MaxSegmentLength"/>、无控制字符。
    /// 额外拒绝 '/'：否则 "a/b" + "c" 与 "a" + "b/c" 会拼成同一个注册表键。
    /// </summary>
    internal static bool IsValidSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return false;

        var trimmed = segment.Trim();
        return trimmed.Length <= MaxSegmentLength
               && trimmed.All(c => !char.IsControl(c) && c != '/');
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
