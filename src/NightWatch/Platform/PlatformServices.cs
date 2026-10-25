namespace NightWatch.Platform;

/// <summary>
/// 按当前操作系统选择平台实现。业务代码只依赖接口，不直接触碰 Windows/Linux API。
/// </summary>
public static class PlatformServices
{
    public static IServiceControl CreateServiceControl()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsServiceControl();
        if (OperatingSystem.IsLinux())
            return new SystemdServiceControl();
        throw new PlatformNotSupportedException($"暂不支持在 {Environment.OSVersion.Platform} 上管理服务单元");
    }

    public static ISystemMetrics CreateSystemMetrics()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsSystemMetrics();
        if (OperatingSystem.IsLinux())
            return new LinuxSystemMetrics();
        throw new PlatformNotSupportedException($"暂不支持在 {Environment.OSVersion.Platform} 上采集系统资源");
    }

    public static IPermissionAuditor CreatePermissionAuditor()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsAclAuditor();
        if (OperatingSystem.IsLinux())
            return new LinuxPermissionAuditor();
        throw new PlatformNotSupportedException($"暂不支持在 {Environment.OSVersion.Platform} 上进行权限自检");
    }

    /// <summary>路径比较规则：Windows 文件系统大小写不敏感，Linux 敏感。</summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>加固提示文案（icacls / chown+chmod）。</summary>
    public static string HardeningHint
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return new WindowsAclAuditor().HardeningHint;
            if (OperatingSystem.IsLinux())
                return new LinuxPermissionAuditor().HardeningHint;
            return "请收紧目标目录权限，仅允许特权账户写入";
        }
    }
}
