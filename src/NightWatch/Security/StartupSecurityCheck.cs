using System.IO;
using NightWatch.Models;
using NightWatch.Platform;

namespace NightWatch.Security;

/// <summary>
/// 部署安全自检（H4）：目标/配置/安装目录对宽泛组可写或 owner 非特权时，
/// 禁用自动拉起（监控与告警保留）并告警。权限读失败按不可信处理。
/// 权限判定与加固提示由 <see cref="IPermissionAuditor"/> 按平台提供。
/// </summary>
public static class StartupSecurityCheck
{
    public static List<string> Check(WatchDogOptions options, Serilog.ILogger logger,
        IPermissionAuditor auditor, bool isDevelopment = false)
    {
        var problems = new List<string>();
        var disableAllLaunch = false;

        if (!isDevelopment)
        {
            foreach (var msg in InspectConfigAndInstall(auditor))
            {
                logger.Error("[安全自检] {Msg}", msg);
                problems.Add(msg);
                disableAllLaunch = true;
            }
        }

        if (disableAllLaunch)
            DisableAllLaunch(options, logger, problems);

        foreach (var rule in options.Processes)
        {
            if (string.IsNullOrWhiteSpace(rule.FilePath))
                continue;
            if (!IsUntrusted(rule.FilePath, auditor, out var where))
                continue;

            DisableLaunch(rule);
            var msg = $"进程守护规则[{rule.Name}]: {rule.FilePath} 的{where}对宽泛组可写或所有者非特权，存在提权风险，已禁用自动拉起（监控保留）。请收紧权限后重新启用：{auditor.HardeningHint}";
            logger.Error("[安全自检] {Msg}", msg);
            problems.Add(msg);
        }

        foreach (var rule in options.FileHeartbeats)
        {
            var hits = new List<string>();
            if (!string.IsNullOrWhiteSpace(rule.FilePath) && IsUntrusted(rule.FilePath, auditor, out var exeWhere))
                hits.Add($"{rule.FilePath} 的{exeWhere}");
            if (!string.IsNullOrWhiteSpace(rule.HeartbeatFile) && IsUntrusted(rule.HeartbeatFile, auditor, out var hbWhere))
                hits.Add($"心跳文件 {rule.HeartbeatFile} 的{hbWhere}");

            if (hits.Count == 0)
                continue;

            DisableLaunch(rule);
            var msg = $"文件心跳规则[{rule.Name}]: {string.Join("；", hits)}对宽泛组可写或所有者非特权，已禁用自动拉起（监控保留）。可写的心跳文件还可被用来掩盖故障或触发杀进程";
            logger.Error("[安全自检] {Msg}", msg);
            problems.Add(msg);
        }

        return problems;
    }

    /// <summary>安装目录或任意 appsettings*.json 对宽泛组可写（开发环境不检查这些位置）。</summary>
    public static bool HasUntrustedConfigOrInstall(IPermissionAuditor auditor, bool isDevelopment = false) =>
        !isDevelopment && InspectConfigAndInstall(auditor).Count > 0;

    public static List<string> InspectConfigAndInstall(IPermissionAuditor auditor)
    {
        var problems = new List<string>();
        var root = AppContext.BaseDirectory;

        if (Directory.Exists(root) && IsUntrustedDirectory(root, auditor, out var installWhere))
            problems.Add($"安装目录 {root} 的{installWhere}对宽泛组可写或所有者非特权，可替换 NightWatch 自身或植入 appsettings.{{Environment}}.json");

        if (!Directory.Exists(root))
            return problems;

        foreach (var configPath in Directory.GetFiles(root, "appsettings*.json"))
        {
            if (IsUntrusted(configPath, auditor, out var configWhere))
                problems.Add($"配置文件 {configPath} 的{configWhere}对宽泛组可写或所有者非特权，可被篡改 FilePath / 自检开关");
        }

        return problems;
    }

    /// <summary>文件本身、所在目录或其祖先目录对宽泛组可写、owner 非特权、或权限读失败。</summary>
    public static bool IsUntrusted(string path, IPermissionAuditor auditor, out string where) =>
        IsUntrusted(path, auditor, PlatformServices.RequiresAncestorDirectoryCheck, out where);

    /// <summary>可注入是否检查祖先链，便于在任意平台单测两套语义（Linux 查链、Windows 靠继承 ACE）。</summary>
    internal static bool IsUntrusted(string path, IPermissionAuditor auditor, bool checkAncestorChain, out string where)
    {
        where = "";
        if (File.Exists(path))
        {
            if (!TryEvaluate(new FileInfo(path), auditor, out var fileWrite, out var fileErr))
            {
                where = $"权限无法读取（{fileErr}，按不可信处理）";
                return true;
            }

            if (fileWrite)
            {
                where = "文件本身";
                return true;
            }
        }

        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return false;

        if (!TryEvaluate(new DirectoryInfo(dir), auditor, out var dirWrite, out var dirErr))
        {
            where = $"目录权限无法读取（{dirErr}，按不可信处理）";
            return true;
        }

        if (dirWrite)
        {
            where = "所在目录";
            return true;
        }

        return checkAncestorChain && TryFindUntrustedAncestor(dir, auditor, out where);
    }

    /// <summary>目录本身或祖先目录链对宽泛组可写、owner 非特权、或权限读失败。</summary>
    public static bool IsUntrustedDirectory(string dir, IPermissionAuditor auditor, out string where) =>
        IsUntrustedDirectory(dir, auditor, PlatformServices.RequiresAncestorDirectoryCheck, out where);

    internal static bool IsUntrustedDirectory(string dir, IPermissionAuditor auditor, bool checkAncestorChain, out string where)
    {
        where = "";
        if (!TryEvaluate(new DirectoryInfo(dir), auditor, out var write, out var err))
        {
            where = $"目录权限无法读取（{err}，按不可信处理）";
            return true;
        }

        if (write)
        {
            where = "目录本身";
            return true;
        }

        return checkAncestorChain && TryFindUntrustedAncestor(dir, auditor, out where);
    }

    /// <summary>
    /// 祖先目录链：从直接父目录开始逐级向上，任一层不可信即返回 true。
    /// Linux 没有 ACL 继承，<c>/opt</c>、<c>/srv</c>、<c>/home</c> 等祖先目录可写时，
    /// root:root 755 的安装目录/目标可执行文件仍能被低权限用户整体替换。
    /// </summary>
    private static bool TryFindUntrustedAncestor(string directory, IPermissionAuditor auditor, out string where)
    {
        where = "";
        DirectoryInfo? parent;
        try
        {
            parent = new DirectoryInfo(Path.GetFullPath(directory)).Parent;
        }
        catch
        {
            return false;
        }

        if (parent is null)
            return false;

        foreach (var ancestor in EnumerateDirectoryChain(parent.FullName))
        {
            if (!TryEvaluate(new DirectoryInfo(ancestor), auditor, out var write, out var error))
            {
                where = $"祖先目录 {ancestor} 权限无法读取（{error}，按不可信处理）";
                return true;
            }

            if (write)
            {
                // 保留尾随空格，与既有 "的{where}对宽泛组可写" 模板拼出可读的消息
                where = $"祖先目录 {ancestor} ";
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 由近及远列出目录及其祖先，直到文件系统根；<paramref name="isFileSystemBoundary"/> 为 true 的
    /// 目录（Linux 挂载点）只检查到该层为止。边界可注入，便于在任意平台单测终止条件。
    /// </summary>
    internal static IEnumerable<string> EnumerateDirectoryChain(
        string directory, Func<string, bool>? isFileSystemBoundary = null)
    {
        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(Path.GetFullPath(directory));
        }
        catch
        {
            yield break;
        }

        var boundary = isFileSystemBoundary ?? MountPoints.IsMountPoint;
        while (current is not null)
        {
            yield return current.FullName;
            if (boundary(current.FullName))
                yield break;

            current = current.Parent;
        }
    }

    private static void DisableAllLaunch(WatchDogOptions options, Serilog.ILogger logger, List<string> problems)
    {
        foreach (var rule in options.Processes)
            DisableLaunch(rule);
        foreach (var rule in options.FileHeartbeats)
            DisableLaunch(rule);
        foreach (var rule in options.Services)
            DisableLaunch(rule);

        var msg = "配置或安装位置不可信，已禁用全部进程守护、文件心跳与服务守护的自动拉起（监控与告警继续运行）";
        logger.Error("[安全自检] {Msg}", msg);
        if (!problems.Contains(msg))
            problems.Add(msg);
    }

    private static void DisableLaunch(ProcessRule rule) => rule.AllowLaunch = false;

    private static void DisableLaunch(HeartbeatRule rule) => rule.AllowLaunch = false;

    private static void DisableLaunch(ServiceRule rule) => rule.AllowLaunch = false;

    private static bool TryEvaluate(FileSystemInfo info, IPermissionAuditor auditor,
        out bool writable, out string error)
    {
        var ok = auditor.HasBroadUserWrite(info, out writable, out error);
        if (!ok)
            Serilog.Log.Warning("[安全自检] 无法读取 {Path} 的权限: {Error}，按不可信处理", info.FullName, error);
        return ok;
    }
}
