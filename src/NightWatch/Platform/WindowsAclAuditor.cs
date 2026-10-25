using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using NightWatch.Security;

namespace NightWatch.Platform;

/// <summary>
/// Windows 权限审计：复用 <see cref="AclEvaluator"/> 的 SID/ACL 判定。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAclAuditor : IPermissionAuditor
{
    public bool HasBroadUserWrite(FileSystemInfo info, out bool writable, out string error)
    {
        writable = false;
        error = "";
        try
        {
            FileSystemSecurity acl = info switch
            {
                FileInfo file => file.GetAccessControl(),
                DirectoryInfo dir => dir.GetAccessControl(),
                _ => throw new InvalidOperationException(info.GetType().Name)
            };
            writable = AclEvaluator.HasBroadUserWrite(acl);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public string HardeningHint =>
        "icacls \"<目标目录>\" /inheritance:r /grant:r \"Administrators:(OI)(CI)F\" /grant:r \"SYSTEM:(OI)(CI)F\" /grant:r \"Users:(OI)(CI)RX\"";
}
