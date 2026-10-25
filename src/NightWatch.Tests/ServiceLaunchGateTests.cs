using NightWatch.Models;
using NightWatch.Monitoring;
using NightWatch.Platform;
using Xunit;

namespace NightWatch.Tests;

public class ServiceLaunchGateTests
{
    [Theory]
    [InlineData(true, true, ServiceUnitStatus.Stopped, true)]
    [InlineData(true, true, ServiceUnitStatus.Paused, true)]
    [InlineData(true, false, ServiceUnitStatus.Stopped, false)]
    [InlineData(false, true, ServiceUnitStatus.Stopped, false)]
    [InlineData(true, true, ServiceUnitStatus.Running, false)]
    [InlineData(true, true, ServiceUnitStatus.StartPending, false)]
    [InlineData(true, true, ServiceUnitStatus.NotFound, false)]
    public void AutoRestart_RequiresAllowLaunch(bool autoRestart, bool allowLaunch, ServiceUnitStatus status, bool expected)
    {
        var rule = new ServiceRule { AutoRestart = autoRestart, AllowLaunch = allowLaunch };
        Assert.Equal(expected, ServiceMonitor.CanAttemptRestart(rule, status));
    }
}
