namespace NightWatch.Monitoring;

/// <summary>
/// 进程守护与文件心跳共用的自动拉起退避。
/// <see cref="State.Attempts"/> 统计的是自上次健康以来的拉起次数，包含启动成功但目标仍未恢复的次数，
/// 不是单纯的启动异常次数。目标恢复健康后应 <see cref="Reset"/>。
/// </summary>
internal sealed class RestartBackoff
{
    public const int MaxAttempts = 5;

    private sealed class State
    {
        public DateTime LastLaunchUtc;
        public int Attempts;
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>时钟：默认 <see cref="DateTime.UtcNow"/>；测试注入假时钟，无需等真实分钟级窗口。</summary>
    private readonly Func<DateTime> _utcNow;

    public RestartBackoff()
        : this(static () => DateTime.UtcNow)
    {
    }

    /// <summary>注入时钟，仅用于测试（生产一律用默认构造函数）。</summary>
    internal RestartBackoff(Func<DateTime> utcNow)
    {
        _utcNow = utcNow;
    }

    /// <summary>
    /// 是否允许本次拉起。<paramref name="fused"/> 为 true 表示已达尝试上限，调用方应停止自动拉起。
    /// </summary>
    public bool TryClaim(string key, double staleMinutes, out bool fused)
    {
        fused = false;
        if (!_states.TryGetValue(key, out var state))
            return true;

        if (state.Attempts >= MaxAttempts)
        {
            fused = true;
            return false;
        }

        var delay = TimeSpan.FromMinutes(Math.Max(1, staleMinutes) * (1 << Math.Min(state.Attempts, 2)));
        return _utcNow() - state.LastLaunchUtc >= delay;
    }

    public void NoteAttempt(string key)
    {
        if (!_states.TryGetValue(key, out var state))
            state = _states[key] = new State();
        state.LastLaunchUtc = _utcNow();
        state.Attempts++;
    }

    public void Reset(string key) => _states.Remove(key);
}
