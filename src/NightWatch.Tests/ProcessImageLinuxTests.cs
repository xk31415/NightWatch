using System.Diagnostics;
using System.Runtime.Versioning;
using NightWatch.Monitoring;
using Xunit;

namespace NightWatch.Tests;

/// <summary>
/// Linux 专属的进程识别用例（Windows 上自动跳过）。
/// </summary>
public class ProcessImageLinuxTests
{
    /// <summary>
    /// 回归：Linux 的进程名取自 /proc/&lt;pid&gt;/stat 的 comm，内核截断到 15 字符。
    /// 起一个名字远超 15 字符的进程，确认 FindTargets 能按映像路径命中、也能按名字命中
    /// （名字枚举落空后回退全量扫描），否则长名 exe 会被判“不存在”而每轮重复拉起。
    /// </summary>
    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void FindTargets_FindsProcess_WhenNameExceedsCommLimit()
    {
        // 必须复制「独立」二进制：Ubuntu 24.04 起 coreutils 是多合一程序
        // （/usr/bin/sleep 指向 coreutils，按 argv[0] 分发），复制改名后再执行会报
        // "coreutils: unknown program '...'" 并立刻退出。bash/dash 是独立程序，改名不受影响。
        // 用复制而不是软链，保证 /proc/<pid>/exe 指向这个长名路径，路径匹配才有意义。
        var shell = new[] { "/usr/bin/bash", "/bin/bash", "/usr/bin/dash", "/bin/dash" }
            .FirstOrDefault(File.Exists);
        Assert.False(shell is null, "找不到独立的 shell 二进制，无法构造长名进程");

        // 工作目录放在测试宿主的输出目录（与测试进程同一卷，必然可执行）：
        // 部分 CI 镜像把 /tmp 挂成 noexec，复制出的二进制会 fork 成功但 exec 失败、进程立刻退出。
        var dir = Path.Combine(AppContext.BaseDirectory, "nw-longname-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "nightwatch-long-process-name");

        try
        {
            File.Copy(shell!, exe);
            File.SetUnixFileMode(exe,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // 注意：不能用 bash -c "sleep 30"——bash 会把最后一条外部命令直接 exec 掉
            // （自身被 sleep 替换），/proc/<pid>/exe 就不再指向这个长名路径。
            // 用复合命令让 bash 自己常驻，sleep 只作为子进程。
            using var proc = Process.Start(new ProcessStartInfo(exe)
            {
                ArgumentList = { "-c", "while :; do sleep 1; done" },
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            Assert.NotNull(proc);

            // exec 失败（权限/格式/多合一程序改名等）时进程会立刻退出；
            // 明确报出来，避免误诊成进程识别逻辑的问题。
            if (proc!.WaitForExit(500))
            {
                var err = proc.StandardError.ReadToEnd().Trim();
                var length = new FileInfo(exe).Length;
                Assert.Fail($"复制的二进制启动后立即退出（exit={proc.ExitCode}, {length} bytes, " +
                            $"源={shell}, stderr={err}）：{dir}");
            }

            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                var found = new List<Process>();
                while (DateTime.UtcNow < deadline)
                {
                    found = ProcessImage.FindTargets(null, exe);
                    if (found.Any(p => p.Id == proc!.Id))
                        break;

                    foreach (var p in found) p.Dispose();
                    found.Clear();
                    Thread.Sleep(100);
                }

                try
                {
                    Assert.Contains(found, p => p.Id == proc!.Id);
                }
                finally
                {
                    foreach (var p in found) p.Dispose();
                }

                // 只配 ProcessName（不配 FilePath）的规则同样受 comm 截断影响：
                // 按全名枚举不到，必须回退全量扫描并按 15 字符前缀匹配。
                var byNameOnly = ProcessImage.FindTargets("nightwatch-long-process-name", null);
                try
                {
                    Assert.Contains(byNameOnly, p => p.Id == proc!.Id);
                }
                finally
                {
                    foreach (var p in byNameOnly) p.Dispose();
                }
            }
            finally
            {
                try { proc!.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                proc!.WaitForExit(5000);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 忽略清理失败 */ }
        }
    }
}
