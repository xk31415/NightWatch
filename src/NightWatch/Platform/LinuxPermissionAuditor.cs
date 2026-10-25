using System.Diagnostics;
using System.Runtime.Versioning;

namespace NightWatch.Platform;

/// <summary>
/// Linux 权限审计：
/// - 所有者非特权（uid 非 0 且 ≥ 1000，即普通登录用户）→ 不可信（可 chmod/替换内容）；
/// - 其他用户可写（other-write，等价 Windows 的 Everyone/Users 可写）→ 不可信。
/// 属主组可写视为自定义部署组，放行（对应 Windows 自检只拦宽泛组、不拦 APP_DEPLOYERS）。
/// </summary>
public sealed class LinuxPermissionAuditor : IPermissionAuditor
{
    [SupportedOSPlatform("linux")]
    public bool HasBroadUserWrite(FileSystemInfo info, out bool writable, out string error)
    {
        writable = false;
        error = "";
        try
        {
            var mode = File.GetUnixFileMode(info.FullName);
            var uid = GetOwnerUid(info.FullName);
            writable = IsUntrusted(mode, uid);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public string HardeningHint =>
        "chown -R root:root \"<目标目录>\" && chmod -R go-w \"<目标目录>\"";

    /// <summary>
    /// 纯判定逻辑，便于单测：owner 非特权或 other 可写即不可信。
    /// uid &lt; 1000 视为系统账户（对应 Windows 的 SYSTEM / NT SERVICE\* / TrustedInstaller）。
    /// </summary>
    internal static bool IsUntrusted(UnixFileMode mode, int uid)
    {
        var ownerUnprivileged = uid != 0 && uid >= 1000;
        var worldWritable = (mode & UnixFileMode.OtherWrite) != 0;
        return ownerUnprivileged || worldWritable;
    }

    /// <summary>用 coreutils 的 stat 取属主 uid；.NET BCL 不暴露 Unix 属主 API。</summary>
    [SupportedOSPlatform("linux")]
    private static int GetOwnerUid(string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "stat",
            Arguments = $"-c %u \"{path}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 stat");
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"stat {path} 失败（退出码 {proc.ExitCode}）");

        return int.Parse(output.Trim());
    }
}
