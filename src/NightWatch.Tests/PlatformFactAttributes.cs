using Xunit;

namespace NightWatch.Tests;

/// <summary>仅在 Windows 上运行的测试（ACL/SID 等 Windows API 在 Linux 会抛平台异常）。</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows only";
    }
}

/// <summary>仅在 Linux 上运行的测试。</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Linux only";
    }
}
