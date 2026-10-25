using System.ServiceProcess;
using NightWatch.Models;
using NightWatch.Monitoring;
using Xunit;

namespace NightWatch.Tests;

public class ServiceLaunchGateTests
{
    [Theory]
    [InlineData(true, true, ServiceControllerStatus.Stopped, true)]
    [InlineData(true, true, ServiceControllerStatus.Paused, true)]
    [InlineData(true, false, ServiceControllerStatus.Stopped, false)]
    [InlineData(false, true, ServiceControllerStatus.Stopped, false)]
    [InlineData(true, true, ServiceControllerStatus.Running, false)]
    [InlineData(true, true, ServiceControllerStatus.StartPending, false)]
    public void AutoRestart_RequiresAllowLaunch(bool autoRestart, bool allowLaunch, ServiceControllerStatus status, bool expected)
    {
        var rule = new ServiceRule { AutoRestart = autoRestart, AllowLaunch = allowLaunch };
        Assert.Equal(expected, WindowsServiceMonitor.CanAttemptRestart(rule, status));
    }
}
