using System.IO;
using System.Security.AccessControl;
using NightWatch.Models;

namespace NightWatch.Security;

/// <summary>
/// 部署安全自检（H4）：目标/配置/安装目录对宽泛组可写或 owner 非特权时，
/// 禁用自动拉起（监控与告警保留）并告警。ACL 读失败按不可信处理。
/// </summary>
public static class StartupSecurityCheck
{
    public static List<string> Check(WatchDogOptions options, Serilog.ILogger logger, bool isDevelopment = false)
    {
        var problems = new List<string>();
        var disableAllLaunch = false;

        if (!isDevelopment)
        {
            foreach (var msg in InspectConfigAndInstall())
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
            if (!IsUntrusted(rule.FilePath, out var where))
                continue;

            DisableLaunch(rule);
            var msg = $"进程守护规则[{rule.Name}]: {rule.FilePath} 的{where}对宽泛组可写或所有者非特权，存在 SYSTEM 提权风险，已禁用自动拉起（监控保留）。请用 icacls 收紧权限后重新启用";
            logger.Error("[安全自检] {Msg}", msg);
            problems.Add(msg);
        }

        foreach (var rule in options.FileHeartbeats)
        {
            var hits = new List<string>();
            if (!string.IsNullOrWhiteSpace(rule.FilePath) && IsUntrusted(rule.FilePath, out var exeWhere))
                hits.Add($"{rule.FilePath} 的{exeWhere}");
            if (!string.IsNullOrWhiteSpace(rule.HeartbeatFile) && IsUntrusted(rule.HeartbeatFile, out var hbWhere))
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
    public static bool HasUntrustedConfigOrInstall(bool isDevelopment = false) =>
        !isDevelopment && InspectConfigAndInstall().Count > 0;

    public static List<string> InspectConfigAndInstall()
    {
        var problems = new List<string>();
        var root = AppContext.BaseDirectory;

        if (Directory.Exists(root) && IsUntrustedDirectory(root, out var installWhere))
            problems.Add($"安装目录 {root} 的{installWhere}对宽泛组可写或所有者非特权，可替换 NightWatch 自身或植入 appsettings.{{Environment}}.json");

        if (!Directory.Exists(root))
            return problems;

        foreach (var configPath in Directory.GetFiles(root, "appsettings*.json"))
        {
            if (IsUntrusted(configPath, out var configWhere))
                problems.Add($"配置文件 {configPath} 的{configWhere}对宽泛组可写或所有者非特权，可被篡改 FilePath / 自检开关");
        }

        return problems;
    }

    /// <summary>文件本身或其父目录对宽泛组可写、owner 非特权、或 ACL 读失败。</summary>
    public static bool IsUntrusted(string path, out string where)
    {
        where = "";
        if (File.Exists(path))
        {
            if (!TryEvaluate(new FileInfo(path), out var fileWrite, out var fileErr))
            {
                where = $"文件 ACL 无法读取（{fileErr}，按不可信处理）";
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

        if (!TryEvaluate(new DirectoryInfo(dir), out var dirWrite, out var dirErr))
        {
            where = $"目录 ACL 无法读取（{dirErr}，按不可信处理）";
            return true;
        }

        if (dirWrite)
        {
            where = "所在目录";
            return true;
        }

        return false;
    }

    public static bool IsUntrustedDirectory(string dir, out string where)
    {
        where = "";
        if (!TryEvaluate(new DirectoryInfo(dir), out var write, out var err))
        {
            where = $"目录 ACL 无法读取（{err}，按不可信处理）";
            return true;
        }

        if (!write)
            return false;

        where = "目录本身";
        return true;
    }

    private static void DisableAllLaunch(WatchDogOptions options, Serilog.ILogger logger, List<string> problems)
    {
        foreach (var rule in options.Processes)
            DisableLaunch(rule);
        foreach (var rule in options.FileHeartbeats)
            DisableLaunch(rule);
        foreach (var rule in options.Services)
            DisableLaunch(rule);

        var msg = "配置或安装位置不可信，已禁用全部进程守护、文件心跳与 Windows 服务的自动拉起（监控与告警继续运行）";
        logger.Error("[安全自检] {Msg}", msg);
        if (!problems.Contains(msg))
            problems.Add(msg);
    }

    private static void DisableLaunch(ProcessRule rule) => rule.AllowLaunch = false;

    private static void DisableLaunch(HeartbeatRule rule) => rule.AllowLaunch = false;

    private static void DisableLaunch(ServiceRule rule) => rule.AllowLaunch = false;

    private static bool TryEvaluate(FileSystemInfo info, out bool writable, out string error)
    {
        writable = false;
        error = "";
        try
        {
            FileSystemSecurity acl = info switch
            {
                FileInfo file => file.GetAccessControl(),
                DirectoryInfo dir => dir.GetAccessControl(),
                _ => throw new InvalidOperationException(info.GetType().Name)
            };
            writable = AclEvaluator.HasBroadUserWrite(acl);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            Serilog.Log.Warning("[安全自检] 无法读取 {Path} 的 ACL: {Error}，按不可信处理", info.FullName, error);
            return false;
        }
    }
}
