using System.Diagnostics;
using System.IO;

namespace NightWatch.Monitoring;

/// <summary>
/// 构造拉起用的 <see cref="ProcessStartInfo"/>。进程守护与文件心跳共用同一份逻辑，
/// 避免两处对“空 WorkingDirectory 怎么处理”的理解不一致。
/// </summary>
internal static class ProcessStartInfoFactory
{
    /// <summary>
    /// 空白（null / 空串 / 全空格）的 WorkingDirectory 回落到可执行文件所在目录。
    /// 不能用 <c>??</c> 判空：示例配置里的 <c>""</c> 表示“未设置”，直接传给 ProcessStartInfo
    /// 会让子进程继承服务的工作目录（C:\Windows\System32），依赖相对路径的程序会启动失败。
    /// </summary>
    public static ProcessStartInfo Create(string filePath, string? arguments, string? workingDirectory)
    {
        var workingDir = string.IsNullOrWhiteSpace(workingDirectory)
            ? Path.GetDirectoryName(filePath) ?? ""
            : workingDirectory;

        return new ProcessStartInfo
        {
            FileName = filePath,
            Arguments = arguments ?? "",
            UseShellExecute = false,
            WorkingDirectory = workingDir
        };
    }
}
