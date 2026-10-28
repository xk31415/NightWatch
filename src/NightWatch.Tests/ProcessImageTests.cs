using System.Diagnostics;
using System.Text;
using NightWatch.Monitoring;
using Xunit;

namespace NightWatch.Tests;

/// <summary>
/// ProcessImage 的进程枚举与回退逻辑。
/// 重点：Linux 上进程名会被内核截断到 15 <b>字节</b>（不是 15 字符），名字枚举落空必须回退全量扫描，
/// 否则长名 exe 会被判定为“不存在”而每个周期重复拉起。
/// </summary>
public class ProcessImageTests
{
    // ---- comm 截断：按 UTF-8 字节，多字节名字不能按 char 截 ----

    /// <summary>
    /// 8 个汉字 = 24 字节，char 口径不会截断（旧实现原样返回，永远匹配不上 comm）；
    /// 字节口径应截到 15 字节 = 5 个汉字。
    /// </summary>
    [Fact]
    public void TruncateToLinuxComm_MultibyteName_TruncatesByUtf8Bytes()
    {
        const string name = "中文进程名称测试";

        var truncated = ProcessImage.TruncateToLinuxComm(name);

        Assert.Equal("中文进程名", truncated); // 5 个汉字 = 15 字节
        Assert.True(Encoding.UTF8.GetByteCount(truncated) <= 15,
            $"截断结果应不超过 15 字节，实际 {Encoding.UTF8.GetByteCount(truncated)} 字节");
        Assert.True(truncated.Length < name.Length, "按 char 判断的旧实现会返回原名，本断言用于锁死回归");
    }

    /// <summary>截断点落在多字节字符中间时，残字节解码为替换字符（与 .NET 读 comm 的解码一致），不应抛异常。</summary>
    [Fact]
    public void TruncateToLinuxComm_SplitCharacter_UsesReplacementChar()
    {
        // "ab" 占 2 字节，剩下 13 字节 = 4 个汉字（12 字节）+ 1 个残字节（第 5 个汉字的首字节）
        const string name = "ab中文进程名称";

        var truncated = ProcessImage.TruncateToLinuxComm(name);

        Assert.StartsWith("ab中文进程", truncated, StringComparison.Ordinal);
        Assert.EndsWith("\uFFFD", truncated, StringComparison.Ordinal);
        Assert.Equal(7, truncated.Length); // 2 个 ASCII + 4 个汉字 + 1 个替换字符
    }

    [Theory]
    [InlineData("nightwatch-long-process-name", "nightwatch-long")]
    [InlineData("short-name", "short-name")]
    [InlineData("exactly15chars1", "exactly15chars1")]
    public void TruncateToLinuxComm_AsciiNames_TruncateAt15Bytes(string name, string expected) =>
        Assert.Equal(expected, ProcessImage.TruncateToLinuxComm(name));

    [Fact]
    public void TruncateToLinuxComm_EmptyName_Unchanged()
    {
        Assert.Equal("", ProcessImage.TruncateToLinuxComm(""));
    }

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
    public void FindTargets_NameOnly_WhenNameLookupMisses_FallsBackToFullScan()
    {
        var byNameCalls = 0;
        var allCalls = 0;

        var result = ProcessImage.FindTargets("nightwatch-long-process-name", null,
            byName: _ => { byNameCalls++; return Array.Empty<Process>(); },
            all: () => { allCalls++; return Array.Empty<Process>(); });

        Assert.Empty(result);
        Assert.Equal(1, byNameCalls);
        Assert.Equal(1, allCalls);
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
