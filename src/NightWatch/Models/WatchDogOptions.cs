namespace NightWatch.Models;

/// <summary>看门狗总配置（对应 appsettings.json 的 WatchDog 节）。</summary>
public class WatchDogOptions
{
    public const string SectionName = "WatchDog";

    public List<ProcessRule> Processes { get; set; } = new();
    public List<ServiceRule> Services { get; set; } = new();
    public List<FileRule> Files { get; set; } = new();

    /// <summary>文件心跳规则（被守护程序定期 touch 心跳文件）。</summary>
    public List<HeartbeatRule> FileHeartbeats { get; set; } = new();
    public ResourceRule Resources { get; set; } = new();
    public NotifyOptions Notify { get; set; } = new();

    /// <summary>HTTP 心跳监控（多产品多用户），为 null 或 Enabled=false 时不启动。</summary>
    public HttpHeartbeatOptions? HttpHeartbeat { get; set; }

    /// <summary>
    /// 全局监控/告警时段；null = 24 小时生效。单条规则自己的 Schedule 会覆盖它。
    /// 为空对象或空 Windows 列表也表示全天生效，可用于让某条规则摆脱全局时段。
    /// </summary>
    public ScheduleRule? Schedule { get; set; }

    /// <summary>
    /// 启用 H4 部署安全自检（默认关闭）。关闭权只认环境变量
    /// WatchDog__EnableStartupSecurityCheck 或命令行；配置文件可写时文件里的 false 无效。
    /// 开启后：宽泛组可写或所有者非特权即禁用自动拉起（监控保留）并告警。
    /// </summary>
    public bool EnableStartupSecurityCheck { get; set; } = false;
}

/// <summary>进程监控规则：进程退出/无响应时自动拉起。</summary>
public class ProcessRule
{
    /// <summary>规则显示名（用于日志与告警）。</summary>
    public string Name { get; set; } = "";

    /// <summary>进程名（不带 .exe，如 "nginx"）。</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>可执行文件完整路径；为空则只告警不拉起。</summary>
    public string? FilePath { get; set; }

    public string? Arguments { get; set; }

    public string? WorkingDirectory { get; set; }

    /// <summary>检查间隔（秒）。</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>同类告警的冷却时间（分钟），防止轰炸。</summary>
    public int CooldownMinutes { get; set; } = 10;

    /// <summary>窗口程序无响应时是否强杀重启。</summary>
    public bool RestartIfNotResponding { get; set; } = false;

    /// <summary>是否允许自动拉起。安全自检失败时会关闭，监控与告警保留。</summary>
    public bool AllowLaunch { get; set; } = true;

    /// <summary>该规则的监控/告警时段；不写则继承全局 Schedule。</summary>
    public ScheduleRule? Schedule { get; set; }
}

/// <summary>Windows 服务监控规则：服务停止时自动启动。</summary>
public class ServiceRule
{
    public string Name { get; set; } = "";

    /// <summary>服务名（sc query 里的 ServiceName，不是显示名）。</summary>
    public string ServiceName { get; set; } = "";

    public bool AutoRestart { get; set; } = true;

    /// <summary>
    /// 是否允许自动启动或继续。安全自检判定配置或安装位置不可信时会关闭，监控与告警保留。
    /// 默认 true，旧配置不写该字段时行为不变。
    /// </summary>
    public bool AllowLaunch { get; set; } = true;

    public int IntervalSeconds { get; set; } = 60;
    public int CooldownMinutes { get; set; } = 10;

    /// <summary>该规则的监控/告警时段；不写则继承全局 Schedule。</summary>
    public ScheduleRule? Schedule { get; set; }
}

/// <summary>文件/日志监控规则：检查日志停更（卡死）与异常膨胀。</summary>
public class FileRule
{
    public string Name { get; set; } = "";

    /// <summary>文件或目录路径。目录时结合 Pattern 取最近修改的文件。</summary>
    public string Path { get; set; } = "";

    public string Pattern { get; set; } = "*";

    /// <summary>文件超过 N 分钟未更新则告警；0 = 关闭该检查。</summary>
    public int StaleMinutes { get; set; } = 0;

    /// <summary>文件超过 N MB 告警；0 = 关闭该检查。</summary>
    public int MaxSizeMB { get; set; } = 0;

    public int IntervalSeconds { get; set; } = 60;
    public int CooldownMinutes { get; set; } = 30;

    /// <summary>该规则的监控/告警时段；不写则继承全局 Schedule。</summary>
    public ScheduleRule? Schedule { get; set; }
}

/// <summary>心跳监控规则：被守护程序定期 touch 心跳文件，超时未更新则告警/重启。</summary>
public class HeartbeatRule
{
    public string Name { get; set; } = "";

    /// <summary>心跳文件路径（被监控程序周期性写入）。</summary>
    public string HeartbeatFile { get; set; } = "";

    /// <summary>超过 N 分钟未更新视为失联。</summary>
    public int StaleMinutes { get; set; } = 5;

    /// <summary>可选：失联时要拉起的进程名（不带 .exe）。</summary>
    public string? ProcessName { get; set; }

    /// <summary>可选：失联时要启动的可执行文件路径。</summary>
    public string? FilePath { get; set; }
    public string? Arguments { get; set; }

    public int IntervalSeconds { get; set; } = 60;
    public int CooldownMinutes { get; set; } = 10;

    /// <summary>是否允许心跳超时后杀进程并拉起。安全自检失败时会关闭，监控与告警保留。</summary>
    public bool AllowLaunch { get; set; } = true;

    /// <summary>该规则的监控/告警时段；不写则继承全局 Schedule。</summary>
    public ScheduleRule? Schedule { get; set; }
}

/// <summary>系统资源监控规则：CPU / 内存 / 磁盘阈值。</summary>
public class ResourceRule
{
    public int IntervalSeconds { get; set; } = 60;
    public int CooldownMinutes { get; set; } = 30;

    /// <summary>CPU 使用率阈值（%），超过则告警；0 = 关闭。</summary>
    public double CpuPercent { get; set; } = 0;

    /// <summary>内存使用率阈值（%），超过则告警；0 = 关闭。</summary>
    public double MemoryPercent { get; set; } = 0;

    /// <summary>磁盘剩余空间阈值检查。</summary>
    public List<DiskRule> Disks { get; set; } = new();

    /// <summary>资源监控的时段；不写则继承全局 Schedule。</summary>
    public ScheduleRule? Schedule { get; set; }
}

public class DiskRule
{
    /// <summary>盘符，如 "C:" 或 "C:\"。</summary>
    public string Drive { get; set; } = "C:";

    /// <summary>剩余空间低于 N GB 告警。</summary>
    public double MinFreeGB { get; set; } = 10;
}

/// <summary>
/// HTTP 心跳监控配置：被守护程序通过 HTTP 上报 "产品/用户" 心跳，
/// NightWatch 检查每个键的最近上报时间，超时即告警。
/// </summary>
public class HttpHeartbeatOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>HTTP 监听地址：默认 localhost（仅本机）；需要接收其他机器心跳时改为 0.0.0.0 并务必配置 Token。</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>HTTP 监听端口。</summary>
    public int Port { get; set; } = 5200;

    /// <summary>
    /// 访问令牌：配置后，上报与状态查询请求必须携带
    /// X-NightWatch-Token 头 / Authorization: Bearer 头 / ?token= 之一。空 = 不鉴权（仅建议 localhost 使用）。
    /// </summary>
    public string? Token { get; set; }

    /// <summary>默认心跳超时阈值（分钟，支持小数）；可按产品覆盖。</summary>
    public double StaleMinutes { get; set; } = 5;

    /// <summary>检查间隔（秒）。</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>同类告警冷却（分钟）。</summary>
    public int CooldownMinutes { get; set; } = 10;

    /// <summary>动态心跳键的保留时间（分钟，支持小数）；超时未上报达到该时长后移除，0 = 不清理。</summary>
    public double RetentionMinutes { get; set; } = 60;

    /// <summary>必达清单（格式 "product/user"）：从未上报过也会告警，不受保留期清理影响。</summary>
    public List<string> Expected { get; set; } = new();

    /// <summary>
    /// 最多保留的动态心跳键数量。键来自 URL，任何能访问端点的人都能创建；
    /// 达到上限后拒绝新键（已有键仍可正常刷新），避免无界写入撑爆内存。0 = 不限制。
    /// </summary>
    public int MaxKeys { get; set; } = 10_000;

    /// <summary>按产品覆盖超时阈值。</summary>
    public List<ProductStaleOverride> Products { get; set; } = new();

    /// <summary>HTTP 心跳检查的时段；不写则继承全局 Schedule。</summary>
    public ScheduleRule? Schedule { get; set; }
}

/// <summary>某产品专属的心跳超时阈值。</summary>
public class ProductStaleOverride
{
    /// <summary>产品名（与心跳 URL 中的 {product} 一致）。</summary>
    public string Name { get; set; } = "";

    public double StaleMinutes { get; set; } = 5;
}

/// <summary>通知配置：可同时配置多个推送通道。</summary>
public class NotifyOptions
{
    /// <summary>
    /// 告警正文里展示的主机名。多台机器共用一个群时用于区分来源。
    /// 留空则使用 <see cref="Environment.MachineName"/>。
    /// </summary>
    public string? HostLabel { get; set; }

    /// <summary>
    /// 是否在告警中单独展示本机 IPv4（「IP：…」一行）。默认 true。
    /// </summary>
    public bool IncludeHostIp { get; set; } = true;

    /// <summary>推送通道列表（企业微信 / 钉钉 / 邮件可叠加）。</summary>
    public List<NotifyChannel> Channels { get; set; } = new();
}

/// <summary>单个通知通道配置。</summary>
public class NotifyChannel
{
    /// <summary>通道类型：wecom = 企业微信群机器人；dingtalk = 钉钉机器人；email = SMTP 邮件。</summary>
    public string Type { get; set; } = "wecom";

    /// <summary>通道显示名（日志用）。</summary>
    public string Name { get; set; } = "";

    /// <summary>wecom / dingtalk：机器人 Webhook 地址。</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>dingtalk 加签密钥（机器人安全设置选"加签"时必填）。</summary>
    public string? Secret { get; set; }

    // ---- email 通道专用 ----
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 465;
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>发件人邮箱。</summary>
    public string? From { get; set; }

    /// <summary>收件人列表。</summary>
    public List<string> To { get; set; } = new();
}

/// <summary>
/// 监控/告警时段。不配置（null）或 <see cref="Windows"/> 为空 = 24 小时生效。
/// </summary>
public class ScheduleRule
{
    /// <summary>时段列表；为空表示全天生效（可用于让单条规则摆脱全局时段）。</summary>
    public List<TimeWindow> Windows { get; set; } = new();

    /// <summary>
    /// 时段外的行为：NoAlert（默认）= 继续检查、继续自动拉起，但不发外部通知；
    /// Off = 完全停止检查（不告警、不拉起，也不更新状态）。
    /// </summary>
    public ScheduleMode Mode { get; set; } = ScheduleMode.NoAlert;
}

/// <summary>时段外行为。</summary>
public enum ScheduleMode
{
    /// <summary>继续监控与自动拉起，只是不推送告警（夜间免打扰）。</summary>
    NoAlert,

    /// <summary>完全停止该规则的检查与拉起（非营业时间不监控）。</summary>
    Off
}

/// <summary>
/// 一个时间窗口，按服务器本地时间判定，区间为 [Start, End)。
/// Start &gt; End 表示跨零点（例如 22:00-06:00），<see cref="Days"/> 指开始那天。
/// Start 与 End 相同表示整天。
/// </summary>
public class TimeWindow
{
    /// <summary>生效星期：Mon/Monday..Sun 或 1..7（周一=1），也接受「周一」等中文；为空 = 每天。</summary>
    public List<string> Days { get; set; } = new();

    /// <summary>开始时间 HH:mm（含）。</summary>
    public string Start { get; set; } = "00:00";

    /// <summary>结束时间 HH:mm（不含）。</summary>
    public string End { get; set; } = "00:00";
}
