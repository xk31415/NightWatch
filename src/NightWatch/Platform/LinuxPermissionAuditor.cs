using System.Runtime.Versioning;

namespace NightWatch.Platform;

/// <summary>
/// Linux 权限审计：
/// - 所有者不是 root → 不可信（Linux 上只有 root 是特权账户；www-data=33、nginx 等 uid &lt; 1000
///   的服务账户同样能改写自己的文件，不能当特权账户放行）；
/// - 其他用户可写（other-write，等价 Windows 的 Everyone/Users 可写）→ 不可信。
/// 属主组可写视为自定义部署组，放行（对应 Windows 自检只拦宽泛组、不拦 APP_DEPLOYERS）。
/// 注意：不检测 POSIX ACL（setfacl）与属组可写，这两类写权限需人工保证。
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
    /// 纯判定逻辑，便于单测：owner 不是 root 或 other 可写即不可信。
    /// Linux 只有 uid 0 能 chmod/替换任意文件；uid &lt; 1000 只是「系统账户」，不是特权账户
    /// （与 Windows 侧「自定义服务账户也算非特权」的判定对齐）。
    /// </summary>
    internal static bool IsUntrusted(UnixFileMode mode, int uid)
    {
        var ownerUnprivileged = uid != 0;
        var worldWritable = (mode & UnixFileMode.OtherWrite) != 0;
        return ownerUnprivileged || worldWritable;
    }

    /// <summary>
    /// 用 coreutils 的 stat 取属主 uid（.NET BCL 不暴露 Unix 属主 API）。
    /// 走统一的 <see cref="ProcessRunner"/>：ArgumentList 传参、15 秒超时、捕获 stderr。
    /// <see cref="IPermissionAuditor.HasBroadUserWrite"/> 是同步接口，调用链上没有可用的取消令牌，
    /// 这里显式用 <see cref="CancellationToken.None"/>：stat 是本地短命令，15 秒超时足够兜底。
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static int GetOwnerUid(string path)
    {
        var result = ProcessRunner.Run("stat", ["-c", "%u", path], ct: CancellationToken.None);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"stat {path} 失败（退出码 {result.ExitCode}）: {result.Stderr.Trim()}");

        var text = result.Stdout.Trim();
        if (!int.TryParse(text, out var uid))
            throw new InvalidOperationException($"stat {path} 输出无法解析: 「{text}」");

        return uid;
    }
}
