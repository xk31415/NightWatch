using NightWatch.Models;
using NightWatch.Monitoring;
using Xunit;

namespace NightWatch.Tests;

public class ProcessMonitorHungWarningTests
{
    [Fact]
    public void OnLinux_ReturnsOnlyRulesWithRestartIfNotResponding()
    {
        var rules = new[]
        {
            new ProcessRule { Name = "a", RestartIfNotResponding = true },
            new ProcessRule { Name = "b", RestartIfNotResponding = false },
            new ProcessRule { Name = "c", RestartIfNotResponding = true }
        };

        Assert.Equal(new[] { "a", "c" }, ProcessMonitor.RulesIgnoringHungRestart(rules, isLinux: true));
    }

    [Fact]
    public void OnWindows_ReturnsNothing()
    {
        var rules = new[] { new ProcessRule { Name = "a", RestartIfNotResponding = true } };

        Assert.Empty(ProcessMonitor.RulesIgnoringHungRestart(rules, isLinux: false));
    }

    [Fact]
    public void OnLinux_NoRuleConfigured_ReturnsEmpty()
    {
        var rules = new[] { new ProcessRule { Name = "a" } };

        Assert.Empty(ProcessMonitor.RulesIgnoringHungRestart(rules, isLinux: true));
    }
}
