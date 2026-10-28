using NightWatch.Models;
using NightWatch.Platform;
using NightWatch.Security;
using Serilog;
using Xunit;

namespace NightWatch.Tests;

/// <summary>
/// 自检结论如何「落到规则上」：这是安全自检真正起作用的地方——
/// 判定不可信却忘了把 <c>AllowLaunch</c> 置 false，等于自检跑了个寂寞（静默的安全回退）。
/// 用 fake <see cref="IPermissionAuditor"/> 构造场景，不依赖真实文件权限；
/// 真实的 ACL / uid 语义由 AclEvaluatorTests、LinuxPlatformLogicTests、StartupSecurityCheckTests 覆盖。
/// </summary>
public class StartupSecurityCheckPropagationTests
{
    private static readonly Serilog.ILogger Logger = new LoggerConfiguration().CreateLogger();

    /// <summary>
    /// 安装目录（<see cref="AppContext.BaseDirectory"/>）不可信 → 三类规则的自动拉起全部关闭。
    /// 去掉 <c>DisableAllLaunch</c> 的传播时本用例必须变红。
    /// </summary>
    [Fact]
    public void UntrustedInstallDirectory_DisablesAllThreeRuleKinds()
    {
        using var temp = new TempDir();
        var options = new WatchDogOptions
        {
            Processes = [new ProcessRule { Name = "进程规则", FilePath = temp.NewFile("proc-app.exe") }],
            FileHeartbeats =
            [
                new HeartbeatRule
                {
                    Name = "心跳规则",
                    FilePath = temp.NewFile("hb-app.exe"),
                    HeartbeatFile = temp.NewFile("heartbeat.txt")
                }
            ],
            Services = [new ServiceRule { Name = "服务规则", ServiceName = "nginxd.service" }]
        };

        // 安装目录是 Check 内部唯一不可注入的位置，用路径精确点名它
        var auditor = new ScriptedAuditor(path => SamePath(path, AppContext.BaseDirectory));

        var problems = StartupSecurityCheck.Check(options, Logger, auditor);

        Assert.False(options.Processes[0].AllowLaunch);
        Assert.False(options.FileHeartbeats[0].AllowLaunch);
        Assert.False(options.Services[0].AllowLaunch);
        Assert.Contains(problems, p => p.Contains("安装目录", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("已禁用全部进程守护、文件心跳与服务守护的自动拉起", StringComparison.Ordinal));
    }

    /// <summary>只把某一条目标 exe 报成可写 → 仅该条被禁拉起，其余规则保持可用。</summary>
    [Fact]
    public void SingleWritableExe_DisablesOnlyThatRule()
    {
        using var temp = new TempDir();
        var writableExe = temp.NewFile("writable-app.exe");
        var cleanExe = temp.NewFile("clean-app.exe");
        var options = new WatchDogOptions
        {
            Processes =
            [
                new ProcessRule { Name = "可写规则", FilePath = writableExe },
                new ProcessRule { Name = "干净规则", FilePath = cleanExe }
            ],
            FileHeartbeats =
            [
                new HeartbeatRule
                {
                    Name = "心跳规则",
                    FilePath = cleanExe,
                    HeartbeatFile = temp.NewFile("heartbeat.txt")
                }
            ],
            Services = [new ServiceRule { Name = "服务规则", ServiceName = "nginxd.service" }]
        };

        var auditor = new ScriptedAuditor(path => SamePath(path, writableExe));

        var problems = StartupSecurityCheck.Check(options, Logger, auditor);

        Assert.False(options.Processes[0].AllowLaunch);
        Assert.True(options.Processes[1].AllowLaunch);
        Assert.True(options.FileHeartbeats[0].AllowLaunch);
        Assert.True(options.Services[0].AllowLaunch);

        var problem = Assert.Single(problems);
        Assert.Contains("可写规则", problem, StringComparison.Ordinal);
        Assert.Contains("文件本身", problem, StringComparison.Ordinal);
        Assert.Contains("已禁用自动拉起", problem, StringComparison.Ordinal);
    }

    /// <summary>权限读失败必须按不可信处理（读不到 ≠ 安全），并说明原因。</summary>
    [Fact]
    public void AuditorReadFailure_IsTreatedAsUntrusted()
    {
        using var temp = new TempDir();
        var exe = temp.NewFile("unreadable-app.exe");
        var options = new WatchDogOptions
        {
            Processes = [new ProcessRule { Name = "读失败规则", FilePath = exe }]
        };
        var auditor = new ScriptedAuditor(
            _ => false,
            readError: path => SamePath(path, exe) ? "拒绝访问" : null);

        var problems = StartupSecurityCheck.Check(options, Logger, auditor);

        Assert.False(options.Processes[0].AllowLaunch);
        var problem = Assert.Single(problems);
        Assert.Contains("权限无法读取", problem, StringComparison.Ordinal);
        Assert.Contains("按不可信处理", problem, StringComparison.Ordinal);
        Assert.Contains("拒绝访问", problem, StringComparison.Ordinal);
    }

    /// <summary>开发环境不检查安装目录/配置位置；同一份判定放到生产模式下必须命中。</summary>
    [Fact]
    public void DevelopmentMode_SkipsInstallDirectoryCheck()
    {
        using var temp = new TempDir();
        var options = new WatchDogOptions
        {
            Processes = [new ProcessRule { Name = "进程规则", FilePath = temp.NewFile("app.exe") }],
            Services = [new ServiceRule { Name = "服务规则", ServiceName = "nginxd.service" }]
        };
        var auditor = new ScriptedAuditor(path => SamePath(path, AppContext.BaseDirectory));

        // 开发模式：不产生问题、不禁用拉起
        Assert.Empty(StartupSecurityCheck.Check(options, Logger, auditor, isDevelopment: true));
        Assert.True(options.Processes[0].AllowLaunch);
        Assert.True(options.Services[0].AllowLaunch);

        // 同一份 auditor 在生产模式下必须判定为不可信
        Assert.True(StartupSecurityCheck.HasUntrustedConfigOrInstall(auditor));
        Assert.False(StartupSecurityCheck.HasUntrustedConfigOrInstall(auditor, isDevelopment: true));
    }

    /// <summary>
    /// 心跳文件本身可写 → 禁该心跳规则的拉起。
    /// 可写的心跳文件能被用来掩盖故障或反向触发杀进程，所以这条也要拦。
    /// </summary>
    [Fact]
    public void WritableHeartbeatFile_DisablesHeartbeatRule()
    {
        using var temp = new TempDir();
        var heartbeatFile = temp.NewFile("heartbeat.txt");
        var options = new WatchDogOptions
        {
            FileHeartbeats =
            [
                new HeartbeatRule
                {
                    Name = "心跳规则",
                    FilePath = temp.NewFile("hb-app.exe"),
                    HeartbeatFile = heartbeatFile
                }
            ]
        };
        var auditor = new ScriptedAuditor(path => SamePath(path, heartbeatFile));

        var problems = StartupSecurityCheck.Check(options, Logger, auditor);

        Assert.False(options.FileHeartbeats[0].AllowLaunch);
        var problem = Assert.Single(problems);
        Assert.Contains("心跳文件", problem, StringComparison.Ordinal);
        Assert.Contains("可写的心跳文件还可被用来掩盖故障或触发杀进程", problem, StringComparison.Ordinal);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(a),
            Path.TrimEndingDirectorySeparator(b),
            PlatformServices.PathComparison);

    /// <summary>按路径脚本化判定；可选让指定路径「读权限失败」。</summary>
    private sealed class ScriptedAuditor(
        Func<string, bool> isUntrusted,
        Func<string, string?>? readError = null) : IPermissionAuditor
    {
        public bool HasBroadUserWrite(FileSystemInfo info, out bool writable, out string error)
        {
            var scripted = readError?.Invoke(info.FullName);
            if (scripted is not null)
            {
                writable = false;
                error = scripted;
                return false;
            }

            writable = isUntrusted(info.FullName);
            error = "";
            return true;
        }

        public string HardeningHint => "chown -R root:root /opt/nightwatch";
    }

    /// <summary>临时目录：只需要「文件存在且父目录存在」，不写真实 ACL。</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "nw-selfcheck-matrix-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string NewFile(string name)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, "x");
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // 清理失败不影响断言结果
            }
        }
    }
}
