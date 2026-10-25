namespace NightWatch;

/// <summary>
/// 手工解析 <c>--name value</c> 与 <c>--name=value</c>。
/// 不能用配置系统的 CommandLine 提供程序取值：它会把 <c>--test-notify</c> 后面的 token 当成自己的值，
/// 于是 <c>--test-notify --test-message "文案"</c> 里的文案被吞掉、取到空串（测试消息正文变空）。
/// </summary>
internal static class CommandLineArgs
{
    /// <summary>取 <c>--{name} value</c> 或 <c>--{name}=value</c> 的值；不存在或没有值时返回 null。</summary>
    public static string? Value(string[] args, string name)
    {
        var longName = "--" + name;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.Equals(longName, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    return null;
                return args[i + 1];
            }

            if (arg.StartsWith(longName + "=", StringComparison.OrdinalIgnoreCase))
                return arg[(longName.Length + 1)..];
        }

        return null;
    }
}
