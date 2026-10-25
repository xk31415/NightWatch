namespace NightWatch.Security;

/// <summary>
/// 自检开关：关闭权只认管理员通道（环境变量 / 命令行）。
/// 配置文件可写时，文件里的 EnableStartupSecurityCheck=false 不能关掉检查。
/// </summary>
public static class SecuritySwitch
{
    public const string EnvName = "WatchDog__EnableStartupSecurityCheck";
    public const string AltEnvName = "WatchDog:EnableStartupSecurityCheck";

    public static bool? ReadAdminOverride(string[] args)
    {
        var env = Environment.GetEnvironmentVariable(EnvName)
                  ?? Environment.GetEnvironmentVariable(AltEnvName);
        if (bool.TryParse(env, out var fromEnv))
            return fromEnv;

        return ReadArgOverride(args);
    }

    public static bool? ReadArgOverride(string[] args)
    {
        foreach (var arg in args)
        {
            var value = TryReadArg(arg);
            if (value is not null)
                return value;
        }

        return null;
    }

    public static bool ShouldRunCheck(
        bool fileOrDefaultEnabled,
        bool? adminOverride,
        bool configLocationUntrusted,
        out string reason)
    {
        if (adminOverride is false)
        {
            reason = "管理员通过环境变量/命令行关闭了部署安全自检";
            return false;
        }

        if (adminOverride is true)
        {
            reason = "管理员通过环境变量/命令行启用了部署安全自检";
            return true;
        }

        if (configLocationUntrusted)
        {
            reason = "配置文件或安装目录对宽泛组可写，忽略文件中的 EnableStartupSecurityCheck=false，强制自检并禁用自动拉起";
            return true;
        }

        if (fileOrDefaultEnabled)
        {
            reason = "配置已启用部署安全自检";
            return true;
        }

        reason = "部署安全自检未启用（EnableStartupSecurityCheck=false）";
        return false;
    }

    private static bool? TryReadArg(string arg)
    {
        const string prefix = "WatchDog:EnableStartupSecurityCheck=";
        const string longPrefix = "--WatchDog:EnableStartupSecurityCheck=";
        string? raw = null;
        if (arg.StartsWith(longPrefix, StringComparison.OrdinalIgnoreCase))
            raw = arg[longPrefix.Length..];
        else if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            raw = arg[prefix.Length..];
        return bool.TryParse(raw, out var value) ? value : null;
    }
}
