using NightWatch.Monitoring;
using Xunit;

namespace NightWatch.Tests;

public class ProcessLaunchCoordinatorTests
{
    [Fact]
    public void TryBeginLaunch_RejectsDuplicateWithinWindow()
    {
        var guard = new ProcessLaunchCoordinator();
        var key = ProcessLaunchCoordinator.MakeKey(null, "MyApp");

        Assert.Equal("name:myapp", key);
        Assert.True(guard.TryBeginLaunch(key));
        Assert.False(guard.TryBeginLaunch(key));
        Assert.False(guard.TryBeginLaunch(key.ToUpperInvariant()));
    }

    [Fact]
    public void TryBeginLaunch_AllowsDistinctKeys()
    {
        var guard = new ProcessLaunchCoordinator();
        Assert.True(guard.TryBeginLaunch(ProcessLaunchCoordinator.MakeKey(" ", "alpha")));
        Assert.True(guard.TryBeginLaunch(ProcessLaunchCoordinator.MakeKey(null, "beta")));
    }

    [Fact]
    public void ForceBeginLaunch_OccupiesTheWindow()
    {
        var guard = new ProcessLaunchCoordinator();
        guard.ForceBeginLaunch("svc");
        Assert.False(guard.TryBeginLaunch("SVC"));
    }

    [Fact]
    public void ConcurrentTryBeginLaunch_AllowsOnlyOne()
    {
        var guard = new ProcessLaunchCoordinator();
        var wins = 0;
        Parallel.For(0, 32, _ =>
        {
            if (guard.TryBeginLaunch("same-target", dedupSeconds: 60))
                Interlocked.Increment(ref wins);
        });
        Assert.Equal(1, wins);
    }
}

public class RestartBackoffTests
{
    [Fact]
    public void NoteAttempt_CountsEveryLaunch_AndBacksOff()
    {
        var backoff = new RestartBackoff();
        Assert.True(backoff.TryClaim("app", staleMinutes: 1, out var fused));
        Assert.False(fused);

        // 启动成功也同样计入尝试次数，避免“Failures”只在异常路径 +1 的误导。
        backoff.NoteAttempt("app");

        Assert.False(backoff.TryClaim("app", staleMinutes: 1, out fused));
        Assert.False(fused);
    }

    [Fact]
    public void MaxAttempts_FusesUntilReset()
    {
        var backoff = new RestartBackoff();
        for (var i = 0; i < RestartBackoff.MaxAttempts; i++)
            backoff.NoteAttempt("app");

        Assert.False(backoff.TryClaim("app", staleMinutes: 1, out var fused));
        Assert.True(fused);

        backoff.Reset("app");
        Assert.True(backoff.TryClaim("app", staleMinutes: 1, out fused));
        Assert.False(fused);
    }
}
