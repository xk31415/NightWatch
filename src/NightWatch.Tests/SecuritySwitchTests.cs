using NightWatch.Security;
using Xunit;

namespace NightWatch.Tests;

public class SecuritySwitchTests
{
    [Fact]
    public void FileFalse_AndLockedConfig_SkipsCheck()
    {
        var run = SecuritySwitch.ShouldRunCheck(false, adminOverride: null, configLocationUntrusted: false, out var reason);
        Assert.False(run);
        Assert.Contains("未启用", reason);
    }

    [Fact]
    public void WritableConfig_IgnoresFileFalse()
    {
        var run = SecuritySwitch.ShouldRunCheck(false, adminOverride: null, configLocationUntrusted: true, out var reason);
        Assert.True(run);
        Assert.Contains("强制自检", reason);
    }

    [Fact]
    public void AdminFalse_WinsEvenIfConfigWritable()
    {
        var run = SecuritySwitch.ShouldRunCheck(true, adminOverride: false, configLocationUntrusted: true, out _);
        Assert.False(run);
    }

    [Fact]
    public void AdminTrue_Enables()
    {
        Assert.True(SecuritySwitch.ShouldRunCheck(false, adminOverride: true, configLocationUntrusted: false, out _));
    }

    [Fact]
    public void CommandLine_ParsesOverride()
    {
        Assert.True(SecuritySwitch.ReadArgOverride(["--WatchDog:EnableStartupSecurityCheck=true"]));
        Assert.False(SecuritySwitch.ReadArgOverride(["WatchDog:EnableStartupSecurityCheck=false"]));
        Assert.Null(SecuritySwitch.ReadArgOverride(["--other"]));
    }
}
