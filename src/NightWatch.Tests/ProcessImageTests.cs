using System.Diagnostics;
using NightWatch.Monitoring;
using Xunit;

namespace NightWatch.Tests;

/// <summary>
/// ProcessImage 的进程枚举与回退逻辑。
/// 重点：Linux 上进程名会被内核截断到 15 字符，名字枚举落空必须回退全量扫描，
/// 否则长名 exe 会被判定为“不存在”而每个周期重复拉起。
/// </summary>
public class ProcessImageTests
{
    [Fact]
    public void FindTargets_WhenNameLookupMisses_FallsBackToFullScan()
    {
        var byNameCalls = 0;
        var allCalls = 0;

        var result = ProcessImage.FindTargets(
            "ignored",
            Path.Combine("opt", "app", "very-long-process-name"),
            byName: _ => { byNameCalls++; return Array.Empty<Process>(); },
            all: () => { allCalls++; return Array.Empty<Process>(); });

        Assert.Empty(result);
        Assert.Equal(1, byNameCalls);
        Assert.Equal(1, allCalls);
    }

    [Fact]
    public void FindTargets_WithoutUsableEnumName_UsesSingleFullScan()
    {
        var byNameCalls = 0;
        var allCalls = 0;
        var directoryOnly = Path.Combine("opt", "app") + Path.DirectorySeparatorChar;

        var result = ProcessImage.FindTargets(null, directoryOnly,
            byName: _ => { byNameCalls++; return Array.Empty<Process>(); },
            all: () => { allCalls++; return Array.Empty<Process>(); });

        Assert.Empty(result);
        Assert.Equal(0, byNameCalls);
        Assert.Equal(1, allCalls);
    }

    [Fact]
    public void FindTargets_NameOnlyRule_DoesNotScanAllProcesses()
    {
        var byNameCalls = 0;
        var allCalls = 0;

        ProcessImage.FindTargets("myapp", null,
            byName: _ => { byNameCalls++; return Array.Empty<Process>(); },
            all: () => { allCalls++; return Array.Empty<Process>(); });

        Assert.Equal(1, byNameCalls);
        Assert.Equal(0, allCalls);
    }

    [Fact]
    public void FindTargets_NothingConfigured_ReturnsEmptyWithoutEnumeration()
    {
        var result = ProcessImage.FindTargets(null, null,
            byName: _ => throw new InvalidOperationException("不应枚举"),
            all: () => throw new InvalidOperationException("不应枚举"));

        Assert.Empty(result);
    }

    [Fact]
    public void FindTargets_MatchesSelf_ByImagePath()
    {
        var self = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(self));

        var found = ProcessImage.FindTargets("definitely-not-this-process", self);
        try
        {
            Assert.Contains(found, p => p.Id == Environment.ProcessId);
        }
        finally
        {
            foreach (var p in found) p.Dispose();
        }
    }
}
