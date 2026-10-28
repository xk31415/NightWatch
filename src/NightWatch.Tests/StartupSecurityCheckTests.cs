using NightWatch.Platform;
using NightWatch.Security;
using Xunit;

namespace NightWatch.Tests;

/// <summary>
/// 部署安全自检的祖先目录链检查与挂载点终止逻辑。
/// 用注入的 <see cref="IPermissionAuditor"/> / 边界判定，不依赖真实文件权限：
/// 核心用例证明「祖先目录可写」在旧实现（只查自身+直接父目录）下会被漏判。
/// </summary>
public class StartupSecurityCheckTests
{
    /// <summary>按路径判定是否「不可信」，用于精确模拟某一层目录可写。</summary>
    private sealed class FakeAuditor(Func<string, bool> isUntrusted) : IPermissionAuditor
    {
        public bool HasBroadUserWrite(FileSystemInfo info, out bool writable, out string error)
        {
            writable = isUntrusted(info.FullName);
            error = "";
            return true;
        }

        public string HardeningHint => "test";
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nw-selfcheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>目标目录自身可信，但祖先目录 evil 可写 → 必须判不可信（旧实现会漏判）。</summary>
    [Fact]
    public void AncestorDirectory_IsChecked_WhenChainEnabled()
    {
        var root = NewTempRoot();
        try
        {
            var target = Path.Combine(root, "evil", "mid", "app");
            Directory.CreateDirectory(target);
            var auditor = new FakeAuditor(p => p.EndsWith("evil", StringComparison.Ordinal));

            var untrusted = StartupSecurityCheck.IsUntrustedDirectory(
                target, auditor, checkAncestorChain: true, out var where);

            Assert.True(untrusted, $"祖先目录 evil 可写应判不可信，where=「{where}」");
            Assert.Contains("evil", where);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>同一场景在旧语义（不查祖先链）下必然漏判 —— 回退修复时本用例会红。</summary>
    [Fact]
    public void AncestorDirectory_WouldBeMissed_WithoutChain()
    {
        var root = NewTempRoot();
        try
        {
            var target = Path.Combine(root, "evil", "mid", "app");
            Directory.CreateDirectory(target);
            var auditor = new FakeAuditor(p => p.EndsWith("evil", StringComparison.Ordinal));

            Assert.False(StartupSecurityCheck.IsUntrustedDirectory(
                target, auditor, checkAncestorChain: false, out var where));
            Assert.Equal("", where);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>配置文件走 IsUntrusted：文件与直接父目录都可信，但再上一层的祖先可写 → 不可信。</summary>
    [Fact]
    public void FileAncestorDirectory_IsChecked_WhenChainEnabled()
    {
        var root = NewTempRoot();
        try
        {
            var dir = Path.Combine(root, "evil", "mid");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "app.bin");
            File.WriteAllText(file, "x");
            var auditor = new FakeAuditor(p => p.EndsWith("evil", StringComparison.Ordinal));

            Assert.True(StartupSecurityCheck.IsUntrusted(file, auditor, checkAncestorChain: true, out var where),
                $"文件祖先目录 evil 可写应判不可信，where=「{where}」");
            Assert.Contains("evil", where);

            // 旧语义（文件 + 直接父目录）只看到 mid，漏掉 evil
            Assert.False(StartupSecurityCheck.IsUntrusted(file, auditor, checkAncestorChain: false, out _));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>所有层都可信时不误判（对应 /、/usr 这类 root 属主且 other 不可写的系统路径）。</summary>
    [Fact]
    public void TrustedChain_IsNotFlagged()
    {
        var root = NewTempRoot();
        try
        {
            var target = Path.Combine(root, "a", "b");
            Directory.CreateDirectory(target);
            var auditor = new FakeAuditor(_ => false);

            Assert.False(StartupSecurityCheck.IsUntrustedDirectory(
                target, auditor, checkAncestorChain: true, out var where));
            Assert.Equal("", where);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void EnumerateDirectoryChain_StopsAtFileSystemBoundary()
    {
        var root = NewTempRoot();
        try
        {
            var target = Path.Combine(root, "a", "b", "c");
            Directory.CreateDirectory(target);
            var boundaryDir = Path.Combine(root, "a", "b");
            var chain = StartupSecurityCheck.EnumerateDirectoryChain(
                target, path => path == boundaryDir).ToList();

            Assert.Equal(new[] { target, boundaryDir }, chain);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void EnumerateDirectoryChain_ReachesFileSystemRoot()
    {
        var root = NewTempRoot();
        try
        {
            var target = Path.Combine(root, "a");
            Directory.CreateDirectory(target);
            var chain = StartupSecurityCheck.EnumerateDirectoryChain(target, _ => false).ToList();

            Assert.Equal(target, chain[0]);
            Assert.Equal(Path.GetPathRoot(target), chain[^1]);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Theory]
    [InlineData("/", "/", true)]
    [InlineData("/data", "/data", true)]
    [InlineData("/data/", "/data", true)]
    [InlineData("/data/app", "/data", false)]
    [InlineData("/datax", "/data", false)]
    public void MountPoint_MatchesNormalizedPath(string candidate, string mountPoint, bool expected) =>
        Assert.Equal(expected, MountPoints.IsMountPointIn([mountPoint], candidate));

    /// <summary>真实 Linux 环境：系统路径属主 root 且 other 不可写，祖先链检查不应误判。</summary>
    [LinuxFact]
    public void LinuxSystemDirectories_AreNotFlaggedByAncestorChain()
    {
        var auditor = new LinuxPermissionAuditor();
        foreach (var dir in new[] { "/usr", "/etc", "/" })
            Assert.False(StartupSecurityCheck.IsUntrustedDirectory(dir, auditor, out var where),
                $"{dir} 被误判为不可信：{where}");
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 清理失败不影响断言结果
        }
    }
}
