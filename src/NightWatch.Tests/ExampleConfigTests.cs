using Microsoft.Extensions.Configuration;
using NightWatch.Models;
using Xunit;

namespace NightWatch.Tests;

/// <summary>
/// 校验两份示例配置能被运行时同款 JSONC 解析器加载并绑定：
/// 示例是给人抄的，抄错字段名或写坏注释都不能只是“看起来对”。
/// </summary>
public class ExampleConfigTests
{
    private static WatchDogOptions Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        Assert.True(File.Exists(path), $"示例配置未复制到输出目录：{fileName}");

        var config = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .Build();

        var options = config.GetSection(WatchDogOptions.SectionName).Get<WatchDogOptions>();
        Assert.NotNull(options);
        return options!;
    }

    [Theory]
    [InlineData("appsettings.example.json")]
    [InlineData("appsettings.example.linux.json")]
    public void ExampleConfig_LoadsAndBinds(string fileName)
    {
        var options = Load(fileName);

        Assert.NotEmpty(options.Processes);
        Assert.NotEmpty(options.Services);
        Assert.NotEmpty(options.Resources.Disks);
        Assert.False(string.IsNullOrWhiteSpace(options.Processes[0].Name));
    }

    [Fact]
    public void LinuxExample_UsesLinuxPathsMountPointsAndSystemdUnit()
    {
        var options = Load("appsettings.example.linux.json");

        var process = options.Processes[0];
        Assert.StartsWith("/", process.FilePath);
        Assert.StartsWith("/", process.WorkingDirectory);
        Assert.StartsWith("/", options.FileHeartbeats[0].HeartbeatFile);

        // Linux 用挂载点而不是盘符；ServiceName 用 systemd unit 名
        Assert.All(options.Resources.Disks, d => Assert.StartsWith("/", d.Drive));
        Assert.EndsWith(".service", options.Services[0].ServiceName);
    }

    [Fact]
    public void WindowsExample_UsesDriveLetters()
    {
        var options = Load("appsettings.example.json");

        Assert.Matches(@"^[A-Za-z]:\\", options.Processes[0].FilePath!);
        Assert.Matches(@"^[A-Za-z]:", options.Resources.Disks[0].Drive);
    }
}
