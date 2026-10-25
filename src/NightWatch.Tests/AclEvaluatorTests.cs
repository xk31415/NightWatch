using System.Security.AccessControl;
using System.Security.Principal;
using NightWatch.Security;
using Xunit;

namespace NightWatch.Tests;

public class AclEvaluatorTests
{
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier Interactive = new(WellKnownSidType.InteractiveSid, null);
    private static readonly SecurityIdentifier ConsoleLogon = new(WellKnownSidType.WinConsoleLogonSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    [Fact]
    public void SidNames_DoNotInclude_ConsoleLogonSid()
    {
        var names = Enum.GetNames<WellKnownSidType>();
        Assert.DoesNotContain("ConsoleLogonSid", names);
        Assert.Contains("WinConsoleLogonSid", names);
        Assert.Contains("InteractiveSid", names);
    }

    [Fact]
    public void ReadAndExecute_IsNotWritable()
    {
        var acl = NewAcl();
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        Assert.False(AclEvaluator.HasBroadAceWrite(acl));
    }

    [Fact]
    public void Modify_IsWritable()
    {
        var acl = NewAcl();
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify, AccessControlType.Allow));
        Assert.True(AclEvaluator.HasBroadAceWrite(acl));
    }

    [Fact]
    public void Interactive_Modify_IsWritable()
    {
        var acl = NewAcl();
        acl.AddAccessRule(new FileSystemAccessRule(Interactive, FileSystemRights.Modify, AccessControlType.Allow));
        Assert.True(AclEvaluator.HasBroadAceWrite(acl));
    }

    [Fact]
    public void ConsoleLogon_Modify_IsWritable()
    {
        var acl = NewAcl();
        acl.AddAccessRule(new FileSystemAccessRule(ConsoleLogon, FileSystemRights.Modify, AccessControlType.Allow));
        Assert.True(AclEvaluator.HasBroadAceWrite(acl));
    }

    [Fact]
    public void DenyWriteData_Plus_AllowFullControl_StillWritable()
    {
        var acl = NewAcl();
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.WriteData, AccessControlType.Deny));
        Assert.True(AclEvaluator.HasBroadAceWrite(acl));
    }

    [Fact]
    public void DenyFullControl_Plus_AllowWrite_IsNotWritable()
    {
        var acl = NewAcl();
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Write, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.FullControl, AccessControlType.Deny));
        Assert.False(AclEvaluator.HasBroadAceWrite(acl));
    }

    [Fact]
    public void PerSid_DenyDoesNotCancel_OtherGroupAllow()
    {
        var acl = NewAcl();
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Modify, AccessControlType.Allow));
        Assert.True(AclEvaluator.HasBroadAceWrite(acl));
    }

    [Fact]
    public void SystemOwner_IsPrivileged()
    {
        Assert.False(AclEvaluator.IsUnprivilegedOwner(System));
        Assert.False(AclEvaluator.IsUnprivilegedOwner(Admins));
        Assert.True(AclEvaluator.IsUnprivilegedOwner(Users));
    }

    [Fact]
    public void UnprivilegedOwner_MakesObjectWritable()
    {
        var acl = NewAcl();
        acl.SetOwner(Users);
        acl.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, AccessControlType.Allow));
        Assert.True(AclEvaluator.HasBroadUserWrite(acl));
    }

    private static FileSecurity NewAcl()
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        return acl;
    }
}
