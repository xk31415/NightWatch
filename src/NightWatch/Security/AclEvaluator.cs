using System.Security.AccessControl;
using System.Security.Principal;

namespace NightWatch.Security;

/// <summary>
/// 用 SID 判定「宽泛组是否对对象拥有只读之外的有效权限」。
/// 按 SID 分别累计 Allow/Deny 位：effective = allow &amp; ~deny，再剥离只读掩码。
/// </summary>
public static class AclEvaluator
{
    /// <summary>
    /// 只读及相关位。不能把 Modify/FullControl 整枚举放进掩码——它们含只读位，
    /// 直接求交集会把纯 RX 误判为可写。
    /// </summary>
    public static readonly FileSystemRights ReadOnlyMask =
        FileSystemRights.ReadData | FileSystemRights.ReadExtendedAttributes |
        FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions |
        FileSystemRights.ExecuteFile | FileSystemRights.Synchronize |
        FileSystemRights.Read | FileSystemRights.ReadAndExecute;

    private static readonly WellKnownSidType[] BroadSidTypes =
    [
        WellKnownSidType.WorldSid,
        WellKnownSidType.BuiltinUsersSid,
        WellKnownSidType.AuthenticatedUserSid,
        WellKnownSidType.InteractiveSid,
        WellKnownSidType.WinConsoleLogonSid,
        WellKnownSidType.WinLocalLogonSid
    ];

    private static readonly WellKnownSidType[] PrivilegedOwnerTypes =
    [
        WellKnownSidType.LocalSystemSid,
        WellKnownSidType.BuiltinAdministratorsSid
    ];

    /// <summary>
    /// NT SERVICE\TrustedInstaller（S-1-5-80-956008885-…）。Windows 组件与 Program Files 等目录的默认所有者，
    /// 属特权服务账户：它不在 WellKnownSidType 里，只按内置枚举判断会把系统目录/系统 exe 误报成“所有者非特权”，
    /// 导致安全自检在加固过的机器上误禁自动拉起。
    /// </summary>
    private static readonly SecurityIdentifier TrustedInstallerSid =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>NT SERVICE\* 服务 SID 前缀（S-1-5-80 及其子授权）。</summary>
    private const string NtServiceSidPrefix = "S-1-5-80";

    public static bool IsBroadUserSid(SecurityIdentifier sid) =>
        BroadSidTypes.Any(sid.IsWellKnown);

    /// <summary>
    /// 所有者是否特权：SYSTEM / Administrators，以及 TrustedInstaller 与 NT SERVICE\* 服务账户。
    /// 这些账户不是普通交互用户，无法被低权限用户冒用。
    /// </summary>
    public static bool IsPrivilegedOwner(SecurityIdentifier sid) =>
        PrivilegedOwnerTypes.Any(sid.IsWellKnown)
        || sid.Equals(TrustedInstallerSid)
        || IsNtServiceSid(sid);

    /// <summary>owner 为宽泛组、或不是特权账户（例如某个普通用户/自定义用户组）时，视为可改 DACL。</summary>
    public static bool IsUnprivilegedOwner(SecurityIdentifier? owner) =>
        owner is not null && (IsBroadUserSid(owner) || !IsPrivilegedOwner(owner));

    private static bool IsNtServiceSid(SecurityIdentifier sid) =>
        sid.Value.Equals(NtServiceSidPrefix, StringComparison.OrdinalIgnoreCase)
        || sid.Value.StartsWith(NtServiceSidPrefix + "-", StringComparison.OrdinalIgnoreCase);

    public static bool HasBroadUserWrite(FileSystemSecurity acl) =>
        IsUnprivilegedOwner(TryGetOwner(acl)) || HasBroadAceWrite(acl);

    /// <summary>只看 DACL 位运算，不含 owner 隐式 WRITE_DAC。供单测与组合判定。</summary>
    public static bool HasBroadAceWrite(FileSystemSecurity acl)
    {
        var allow = new Dictionary<string, FileSystemRights>(StringComparer.OrdinalIgnoreCase);
        var deny = new Dictionary<string, FileSystemRights>(StringComparer.OrdinalIgnoreCase);

        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference is not SecurityIdentifier sid || !IsBroadUserSid(sid))
                continue;

            // 继承项（含 InheritOnly）也参与判定：它决定未来在目录里新建的文件拿到什么权限。
            // 但必须先映射 GENERIC_* 位，否则只读的 GR|GE 会被误判成可写。
            var rights = Normalize(rule.FileSystemRights);
            var key = sid.Value;
            if (rule.AccessControlType == AccessControlType.Allow)
                allow[key] = allow.GetValueOrDefault(key) | rights;
            else if (rule.AccessControlType == AccessControlType.Deny)
                deny[key] = deny.GetValueOrDefault(key) | rights;
        }

        foreach (var key in allow.Keys.Union(deny.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var a = allow.GetValueOrDefault(key);
            var d = deny.GetValueOrDefault(key);
            var effective = (FileSystemRights)((int)a & ~(int)d);
            if ((effective & ~ReadOnlyMask) != 0)
                return true;
        }

        return false;
    }

    private const int GenericAll = 0x10000000;
    private const int GenericExecute = 0x20000000;
    private const int GenericWrite = 0x40000000;
    private const int GenericRead = unchecked((int)0x80000000);

    /// <summary>
    /// 把 GENERIC_* 位映射成具体文件权限位后再做掩码判断。
    /// 系统目录（如 C:\Windows\System32、C:\Program Files）的继承项通常写成 0xA0000000
    /// （GENERIC_READ | GENERIC_EXECUTE），若直接参与运算，纯只读授权会被当成可写，
    /// 导致加固过的安装目录被自检误判为不可信。Deny 同样要映射，否则 Deny 掩不掉泛型位。
    /// </summary>
    internal static FileSystemRights Normalize(FileSystemRights rights)
    {
        var raw = (int)rights;
        if ((raw & GenericAll) != 0)
            return FileSystemRights.FullControl;

        var mapped = raw & ~(GenericRead | GenericWrite | GenericExecute | GenericAll);
        if ((raw & GenericWrite) != 0)
        {
            mapped |= (int)(FileSystemRights.WriteData | FileSystemRights.AppendData
                            | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes);
        }

        if ((raw & GenericRead) != 0)
        {
            mapped |= (int)(FileSystemRights.ReadData | FileSystemRights.ReadAttributes
                            | FileSystemRights.ReadExtendedAttributes | FileSystemRights.ReadPermissions
                            | FileSystemRights.Synchronize);
        }

        if ((raw & GenericExecute) != 0)
        {
            mapped |= (int)(FileSystemRights.ExecuteFile | FileSystemRights.ReadAttributes
                            | FileSystemRights.Synchronize);
        }

        return (FileSystemRights)mapped;
    }

    public static SecurityIdentifier? TryGetOwner(FileSystemSecurity acl)
    {
        try
        {
            return acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch
        {
            return null;
        }
    }
}
