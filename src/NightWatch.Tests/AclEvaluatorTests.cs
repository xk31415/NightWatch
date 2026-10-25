using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using NightWatch.Security;
using Xunit;

namespace NightWatch.Tests;

[SupportedOSPlatform("windows")]
public class AclEvaluatorTests
{
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier Interactive = new(WellKnownSidType.InteractiveSid, null);
    private static readonly SecurityIdentifier ConsoleLogon = new(WellKnownSidType.WinConsoleLogonSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    [WindowsFact]
    public void SidNames_DoNotInclude_ConsoleLogonSid()
    {
        var names = Enum.GetNames<WellKnownSidType>();
        Assert.DoesNotContain("ConsoleLogonSid", names);
        Assert.Contains("WinConsoleLogonSid", names);
        Assert.Contains("InteractiveSid", names);
    }

    [WindowsFact]
    public void ReadAndExecute_IsNotWritable()
    {
        var acl = NewAcl();
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        Assert.False(AclEvaluator.HasBroadAceWrite(acl));
    }

    [WindowsFact]
    public void Modify_IsWritable()
    {
        var acl = NewAcl();
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify, AccessControlType.Allow));
        Assert.True(AclEvaluator.HasBroadAceWrite(acl));
    }

    [WindowsFact]
    public void Interactive_Modify_IsWritable()
    {
        var acl = NewAcl();
        acl.AddAccessRule(new FileSystemAccessRule(Interactive, FileSystemRights.Modify, AccessControlType.Allow));
        Assert.True(AclEvaluator.HasBroadAceWrite(acl));
    }

    [WindowsFact]
    public void ConsoleLogon_Modify_IsWritable()
    {
        var acl = NewAcl();
        acl.AddAccessRule(new FileSystemAccessRule(ConsoleLogon, FileSystemRights.Modify, AccessControlType.Allow));
        Assert.True(AclEvaluator.HasBroadAceWrite(acl));
    }

    [WindowsFact]
    public void DenyWriteData_Plus_AllowFullControl_StillWritable()
    {
        var acl = NewAcl();
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.WriteData, AccessControlType.Deny));
        Assert.True(AclEvaluator.HasBroadAceWrite(acl));
    }

    [WindowsFact]
    public void DenyFullControl_Plus_AllowWrite_IsNotWritable()
    {
        var acl = NewAcl();
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Write, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.FullControl, AccessControlType.Deny));
        Assert.False(AclEvaluator.HasBroadAceWrite(acl));
    }

    [WindowsFact]
    public void PerSid_DenyDoesNotCancel_OtherGroupAllow()
    {
        var acl = NewAcl();
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Modify, AccessControlType.Allow));
        Assert.True(AclEvaluator.HasBroadAceWrite(acl));
    }

    [WindowsFact]
    public void SystemOwner_IsPrivileged()
    {
        Assert.False(AclEvaluator.IsUnprivilegedOwner(System));
        Assert.False(AclEvaluator.IsUnprivilegedOwner(Admins));
        Assert.True(AclEvaluator.IsUnprivilegedOwner(Users));
    }

    [WindowsFact]
    public void UnprivilegedOwner_MakesObjectWritable()
    {
        var acl = NewAcl();
        acl.SetOwner(Users);
        acl.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, AccessControlType.Allow));
        Assert.True(AclEvaluator.HasBroadUserWrite(acl));
    }

    /// <summary>
    /// 回归：TrustedInstaller 是 Windows 组件与 Program Files 等目录的默认所有者。
    /// 它不在 WellKnownSidType 里，修复前会被判成“非特权”，导致 cmd.exe / powershell.exe
    /// 以及加固过的安装目录被误报为不可信，自动拉起被静默禁用。
    /// </summary>
    [WindowsFact]
    public void TrustedInstallerOwner_IsPrivileged()
    {
        var trustedInstaller = new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

        Assert.True(AclEvaluator.IsPrivilegedOwner(trustedInstaller));
        Assert.False(AclEvaluator.IsUnprivilegedOwner(trustedInstaller));

        var acl = NewAcl();
        acl.SetOwner(trustedInstaller);
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        Assert.False(AclEvaluator.HasBroadUserWrite(acl));
    }

    [WindowsFact]
    public void NtServiceOwner_IsPrivileged()
    {
        var serviceSid = new SecurityIdentifier("S-1-5-80-3880718306-3832830129-1677859214-2598158968-1052248003");

        Assert.True(AclEvaluator.IsPrivilegedOwner(serviceSid));
        Assert.False(AclEvaluator.IsUnprivilegedOwner(serviceSid));
    }

    [WindowsFact]
    public void OrdinaryUserOwner_IsStillUnprivileged()
    {
        // 普通本地用户 SID（S-1-5-21-…）：所有者可隐式 WRITE_DAC，仍必须判定为不可信。
        var user = new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-1001");

        Assert.False(AclEvaluator.IsPrivilegedOwner(user));
        Assert.True(AclEvaluator.IsUnprivilegedOwner(user));
    }

    [WindowsFact]
    public void NtServiceGroupSid_IsPrivileged()
    {
        // S-1-5-80 本身是 “NT SERVICE” 组，与域 SID（S-1-5-21-…）不同族
        Assert.True(AclEvaluator.IsPrivilegedOwner(new SecurityIdentifier("S-1-5-80")));
    }

    /// <summary>
    /// 回归：系统目录的继承项以 GENERIC_READ|GENERIC_EXECUTE（0xA0000000）存储。
    /// 泛型位不在只读掩码里，修复前会被当成可写，导致 C:\Program Files、
    /// C:\Windows\System32 及其下的 exe 被误判为不可信。
    /// 注：FileSystemAccessRule 的公开构造函数不接受泛型位（真实 ACE 由内核返回），所以直接测映射函数。
    /// </summary>
    [WindowsFact]
    public void Normalize_GenericReadExecute_HasNoWriteBits()
    {
        var rights = AclEvaluator.Normalize(unchecked((FileSystemRights)0xA0000000));

        Assert.Equal(0, (int)(rights & ~AclEvaluator.ReadOnlyMask));
    }

    [WindowsFact]
    public void Normalize_GenericWrite_HasWriteBits()
    {
        var rights = AclEvaluator.Normalize((FileSystemRights)0x40000000);

        Assert.NotEqual(0, (int)(rights & ~AclEvaluator.ReadOnlyMask));
    }

    [WindowsFact]
    public void Normalize_GenericAll_IsFullControl()
    {
        Assert.Equal(FileSystemRights.FullControl, AclEvaluator.Normalize((FileSystemRights)0x10000000));
    }

    [WindowsFact]
    public void SystemDirectoryAclShape_IsNotWritable()
    {
        // 复刻 C:\Windows\System32 / C:\Program Files 的 ACE 组合：Users 只读 + 只读继承项。
        // 继承项只存在于目录 ACL（FileSecurity 不接受继承标记），所以这里用 DirectorySecurity。
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(
            Users,
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.InheritOnly,
            AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(
            Users, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, AccessControlType.Allow));

        Assert.False(AclEvaluator.HasBroadAceWrite(acl));
    }

    private static FileSecurity NewAcl()
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        return acl;
    }
}
