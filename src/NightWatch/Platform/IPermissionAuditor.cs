namespace NightWatch.Platform;

/// <summary>
/// 跨平台部署安全自检：判定文件/目录是否对「宽泛组」可写或所有者非特权。
/// Windows 走 SID/ACL（<see cref="WindowsAclAuditor"/>），
/// Linux 走属主 uid 与权限位（<see cref="LinuxPermissionAuditor"/>）。
/// </summary>
public interface IPermissionAuditor
{
    /// <summary>
    /// 检查对象是否对宽泛组可写、或所有者非特权。读取失败时 error 非空，应按不可信处理。
    /// </summary>
    bool HasBroadUserWrite(FileSystemInfo info, out bool writable, out string error);

    /// <summary>自检失败时给用户的加固提示（Windows: icacls 示例；Linux: chown/chmod 示例）。</summary>
    string HardeningHint { get; }
}
