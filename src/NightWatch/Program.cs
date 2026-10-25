using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightWatch;
using NightWatch.Heartbeat;
using NightWatch.Monitoring;
using NightWatch.Models;
using NightWatch.Notification;
using NightWatch.Platform;
using NightWatch.Security;
using Serilog;

// ---- 自测模式：NightWatch --test-notify [--test-message 自定义文案] ----
// 按配置向所有通知通道发一条测试消息后退出，用于上线前验证通道连通性。
if (args.Contains("--test-notify"))
{
    var testConfig = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();

    var testOpts = testConfig.GetSection(WatchDogOptions.SectionName).Get<WatchDogOptions>() ?? new WatchDogOptions();

    var consoleFactory = LoggerFactory.Create(b => b.AddSerilog(new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger()));

    var notifier = new MultiChannelNotifier(
        testOpts.Notify, consoleFactory, consoleFactory.CreateLogger<MultiChannelNotifier>());

    // 优先手工解析 --test-message：配置系统会把 --test-notify 的下一个 token 当成它的值，
    // 导致 `--test-notify --test-message "文案"` 取到空串。空值一律回落到默认文案。
    var msg = CommandLineArgs.Value(args, "test-message");
    if (string.IsNullOrWhiteSpace(msg))
        msg = testConfig["test-message"];
    if (string.IsNullOrWhiteSpace(msg))
        msg = "这是一条测试消息，收到即说明通知通道配置正确。";
    var result = await notifier.SendWithResultAsync(new AlertMessage
    {
        Title = "通知通道自测",
        Body = msg,
        IsWarning = false,
        Time = DateTime.Now
    });

    var testLog = consoleFactory.CreateLogger("Test");
    if (result.Total == 0)
    {
        testLog.LogError("没有可用的通知通道：请检查 WatchDog:Notify:Channels（邮件通道还需配置 SmtpHost 与 To）");
        Environment.ExitCode = 1;
        return;
    }

    if (result.Failed > 0)
    {
        foreach (var failure in result.Failures)
            testLog.LogError("通道发送失败: {Failure}", failure);
        testLog.LogError("测试消息发送完成：{Failed}/{Total} 个通道失败，请按上面的原因排查", result.Failed, result.Total);
        Environment.ExitCode = 1;
        return;
    }

    testLog.LogInformation("测试消息已发送至 {Total} 个通道，请到对应群/邮箱确认。", result.Total);
    return;
}

var builder = Host.CreateApplicationBuilder(args);

// ---- 日志：控制台 + 滚动文件（logs/ 目录） ----
builder.Logging.ClearProviders();
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "logs", "nightwatch-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
builder.Logging.AddSerilog(Log.Logger);

// ---- 平台抽象：服务控制 / 资源采集 / 权限审计，按当前 OS 选实现 ----
builder.Services.AddSingleton(PlatformServices.CreateServiceControl());
builder.Services.AddSingleton(PlatformServices.CreateSystemMetrics());
var permissionAuditor = PlatformServices.CreatePermissionAuditor();
builder.Services.AddSingleton(permissionAuditor);

// ---- 服务宿主：Windows 注册为服务，Linux 注册为 systemd unit ----
if (OperatingSystem.IsWindows())
    builder.Services.AddWindowsService(o => o.ServiceName = "NightWatch");
else
    builder.Services.AddSystemd();

// ---- 配置绑定 ----
var options = builder.Configuration
    .GetSection(WatchDogOptions.SectionName)
    .Get<WatchDogOptions>() ?? new WatchDogOptions();
builder.Services.AddSingleton(options);

// ---- 通知器：多通道（企业微信 / 钉钉 / 邮件），经有界队列异步投递 ----
// 单通道最长可阻塞 15 秒；内联发送会拖住同一监控器下所有规则的检查，故用队列解耦。
builder.Services.AddSingleton(sp =>
    new MultiChannelNotifier(options.Notify,
        sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<ILogger<MultiChannelNotifier>>()));
builder.Services.AddSingleton(sp =>
    new QueuedNotifier(sp.GetRequiredService<MultiChannelNotifier>(),
        sp.GetRequiredService<ILogger<QueuedNotifier>>()));
builder.Services.AddSingleton<IEventNotifier>(sp => sp.GetRequiredService<QueuedNotifier>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<QueuedNotifier>());

RuleNameGuard.Normalize(options, Log.Logger);

// ---- 监控时段：校验配置并说明全局时段（未配置 = 24 小时生效） ----
foreach (var problem in Schedule.Validate(options))
    Log.Warning("监控时段配置问题: {Problem}", problem);

if (options.Schedule is { Windows.Count: > 0 })
{
    Log.Information("全局监控时段: {Windows}（时段外{Outside}）", Schedule.Describe(options.Schedule),
        options.Schedule.Mode == ScheduleMode.Off ? "完全停止检查" : "继续监控与拉起、只静默通知");
}

// ---- H4 部署安全自检：关闭权只认环境变量/命令行；配置可写时强制检查并禁用拉起 ----
var rawSwitch = builder.Configuration["WatchDog:EnableStartupSecurityCheck"];
var legacySwitch = builder.Configuration["WatchDog:SkipProcessSecurityCheck"];
if (rawSwitch is null)
{
    if (legacySwitch is not null)
        Log.Warning("检测到已废弃配置键 SkipProcessSecurityCheck={Old}，已被忽略。请改用环境变量 {Env}=true，或配置 EnableStartupSecurityCheck。当前默认关闭",
            legacySwitch, SecuritySwitch.EnvName);
    else
        Log.Warning("未配置 WatchDog:EnableStartupSecurityCheck。多低权限用户共用的机器请设置 {Env}=true。配置文件可写时，文件里的 false 不会被采信",
            SecuritySwitch.EnvName);
}

var isDev = builder.Environment.IsDevelopment();
var adminOverride = SecuritySwitch.ReadAdminOverride(args);
var configUntrusted = StartupSecurityCheck.HasUntrustedConfigOrInstall(permissionAuditor, isDev);
var runSecurityCheck = SecuritySwitch.ShouldRunCheck(
    options.EnableStartupSecurityCheck, adminOverride, configUntrusted, out var switchReason);

List<string> securityProblems = new();
if (!runSecurityCheck)
    Log.Information("{Reason}，跳过权限检查", switchReason);
else
{
    Log.Information("{Reason}", switchReason);
    securityProblems = StartupSecurityCheck.Check(options, Log.Logger, permissionAuditor, isDev);
}

if (securityProblems.Count > 0)
{
    // 不阻塞 host.Run()：SMTP 挂起时 SCM/systemd 会报服务启动超时
    _ = Task.Run(async () =>
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var auditLoggerFactory = LoggerFactory.Create(b => b.AddSerilog(Log.Logger));
            var auditNotifier = new MultiChannelNotifier(options.Notify,
                auditLoggerFactory, auditLoggerFactory.CreateLogger<MultiChannelNotifier>());
            await auditNotifier.SendAsync(new AlertMessage
            {
                Title = "部署安全自检未通过，相关自动拉起已禁用",
                Body = string.Join("\n", securityProblems) +
                       $"\n\n加固示例（管理员执行）:\n{permissionAuditor.HardeningHint}",
                IsWarning = true
            }, cts.Token);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "安全自检告警推送失败（拉起仍保持禁用）");
        }
    });
}

// ---- 按配置注册监控器（未配置的模块不启动） ----
builder.Services.AddSingleton<ProcessLaunchCoordinator>();   // 跨监控器启动去重，防双实例
if (options.Processes.Count > 0)   builder.Services.AddHostedService<ProcessMonitor>();
if (options.Services.Count > 0)    builder.Services.AddHostedService<ServiceMonitor>();
if (options.Files.Count > 0)       builder.Services.AddHostedService<FileLogMonitor>();
if (options.FileHeartbeats.Count > 0) builder.Services.AddHostedService<FileHeartbeatMonitor>();
if (options.Resources.CpuPercent > 0 || options.Resources.MemoryPercent > 0 || options.Resources.Disks.Count > 0)
    builder.Services.AddHostedService<ResourceMonitor>();
if (options.HttpHeartbeat is { Enabled: true })
{
    builder.Services.AddSingleton(options.HttpHeartbeat);
    builder.Services.AddSingleton<HeartbeatRegistry>();
    builder.Services.AddHostedService<HeartbeatApiService>();   // 接收端：HTTP 上报
    builder.Services.AddHostedService<HttpHeartbeatMonitor>();  // 检查端：超时告警
}

var host = builder.Build();

Log.Information("守夜人(NightWatch)启动 | 进程规则 {P} | 服务规则 {S} | 文件规则 {F} | 文件心跳规则 {H} | HTTP心跳 {H2} | 通知通道 {N}",
    options.Processes.Count, options.Services.Count, options.Files.Count,
    options.FileHeartbeats.Count, options.HttpHeartbeat is { Enabled: true } ? "已启用" : "未启用",
    options.Notify.Channels.Count);

host.Run();
Log.CloseAndFlush();
