namespace NightWatch.Platform;

/// <summary>
/// 按当前操作系统选择平台实现。业务代码只依赖接口，不直接触碰 Windows/Linux API。
/// </summary>
public static class PlatformServices
{
    /// <summary>当前是否有完整实现（Windows / Linux）。其它平台启动时会给出明确提示并退出。</summary>
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

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

    /// <summary>
    /// 权限检查是否要沿祖先目录链向上：Linux 没有 ACL 继承，祖先目录可写就能整体替换子项，
    /// 必须逐级查到挂载点/根；Windows 的继承 ACE 已包含在子对象自身 DACL 中，重复检查祖先反而会把
    /// 「仅此目录可创建」这类权限误判成宽泛可写。
    /// </summary>
    public static bool RequiresAncestorDirectoryCheck => !OperatingSystem.IsWindows();

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
