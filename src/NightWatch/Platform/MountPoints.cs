namespace NightWatch.Platform;

/// <summary>
/// 挂载点判定：祖先目录链权限检查的终止边界。进入另一个文件系统后，挂载点之上的目录无法通过
/// 重命名/替换影响挂载点内的内容，所以只需查到挂载点这一层（挂载点本身仍要检查）。
/// .NET 没有直接暴露挂载点，Linux 上 <see cref="DriveInfo.GetDrives"/> 返回的就是挂载点列表；
/// Windows 不查祖先链（见 <see cref="PlatformServices.RequiresAncestorDirectoryCheck"/>）。
/// </summary>
internal static class MountPoints
{
    private static readonly Lazy<HashSet<string>> LinuxMountPoints =
        new(LoadLinuxMountPoints, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>目录是否为挂载点；非 Linux 平台恒为 false。</summary>
    public static bool IsMountPoint(string directoryPath)
    {
        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            return IsMountPointIn(LinuxMountPoints.Value, directoryPath);
        }
        catch
        {
            // 路径非法或枚举失败时按「不是挂载点」处理：继续向上查到根，多查几层只会更严格
            return false;
        }
    }

    /// <summary>
    /// 纯判定：<paramref name="directoryPath"/> 是否恰好等于某个挂载点（双端规范化后比较，
    /// 容忍结尾的分隔符）。抽成纯函数便于在任意平台单测。
    /// </summary>
    internal static bool IsMountPointIn(IEnumerable<string> mountPoints, string directoryPath)
    {
        var normalized = Normalize(directoryPath);
        foreach (var mountPoint in mountPoints)
        {
            if (string.Equals(Normalize(mountPoint), normalized, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static HashSet<string> LoadLinuxMountPoints()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
                set.Add(Normalize(drive.Name));
        }
        catch
        {
            // 枚举失败时退化为「只到文件系统根」：不会漏判安全问题
        }

        return set;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
