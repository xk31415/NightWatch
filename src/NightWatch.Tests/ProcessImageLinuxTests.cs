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
    /// 起一个名字远超 15 字符的进程，确认 FindTargets 仍能按映像路径命中
    /// （名字枚举落空后回退全量扫描），否则长名 exe 会被判“不存在”而每轮重复拉起。
    /// </summary>
    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void FindTargets_FindsProcess_WhenNameExceedsCommLimit()
    {
        var sleep = new[] { "/bin/sleep", "/usr/bin/sleep" }.FirstOrDefault(File.Exists);
        Assert.False(sleep is null, "找不到 sleep 可执行文件，无法构造长名进程");

        var dir = Path.Combine(Path.GetTempPath(), "nw-longname-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "nightwatch-long-process-name");

        try
        {
            File.Copy(sleep!, exe);
            File.SetUnixFileMode(exe,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            using var proc = Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "30" } });
            Assert.NotNull(proc);

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
