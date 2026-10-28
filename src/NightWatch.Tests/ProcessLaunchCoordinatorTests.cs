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
    }

    /// <summary>
    /// 去重键的大小写语义跟随平台：Windows 路径不敏感，Linux 敏感。
    /// 这里用两条仅大小写不同的路径验证，避免 Linux 上两个不同目标共用去重窗口。
    /// </summary>
    [Fact]
    public void PathKeyCaseSensitivity_FollowsPlatform()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "nw-case-probe");
        var lower = ProcessLaunchCoordinator.MakeKey(Path.Combine(baseDir, "app", "x.exe"), "x");
        var upper = ProcessLaunchCoordinator.MakeKey(Path.Combine(baseDir, "APP", "X.EXE"), "x");

        var guard = new ProcessLaunchCoordinator();
        Assert.True(guard.TryBeginLaunch(lower));

        if (OperatingSystem.IsWindows())
            Assert.False(guard.TryBeginLaunch(upper));
        else
            Assert.True(guard.TryBeginLaunch(upper));
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
        Assert.False(guard.TryBeginLaunch("svc"));
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

    /// <summary>注入时钟：退避窗口按时间推进放行，不必在用例里真的等几分钟。</summary>
    [Fact]
    public void BackoffWindow_Expires_WithInjectedClock()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var backoff = new RestartBackoff(() => now);

        Assert.True(backoff.TryClaim("app", staleMinutes: 1, out _));
        backoff.NoteAttempt("app");   // attempts=1 → 窗口 = 1 分钟 × 2

        Assert.False(backoff.TryClaim("app", staleMinutes: 1, out _));

        now = now.AddMinutes(3);
        Assert.True(backoff.TryClaim("app", staleMinutes: 1, out var fused));
        Assert.False(fused);
    }
}
