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

    public static bool IsBroadUserSid(SecurityIdentifier sid) =>
        BroadSidTypes.Any(sid.IsWellKnown);

    public static bool IsPrivilegedOwner(SecurityIdentifier sid) =>
        PrivilegedOwnerTypes.Any(sid.IsWellKnown);

    /// <summary>owner 为宽泛组或非 SYSTEM/Administrators 的 SID 时视为可改 DACL。</summary>
    public static bool IsUnprivilegedOwner(SecurityIdentifier? owner) =>
        owner is not null && (IsBroadUserSid(owner) || !IsPrivilegedOwner(owner));

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

            var key = sid.Value;
            if (rule.AccessControlType == AccessControlType.Allow)
                allow[key] = allow.GetValueOrDefault(key) | rule.FileSystemRights;
            else if (rule.AccessControlType == AccessControlType.Deny)
                deny[key] = deny.GetValueOrDefault(key) | rule.FileSystemRights;
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
