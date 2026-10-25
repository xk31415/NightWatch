using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NightWatch.Platform;

/// <summary>Windows 资源采集：PerformanceCounter 读 CPU，GlobalMemoryStatusEx 读内存。</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemMetrics : ISystemMetrics
{
    private PerformanceCounter? _cpuCounter;
    private bool _hasSample;

    /// <summary>首次取值时的采样窗口：性能计数器需要两次取值才有意义。</summary>
    internal static readonly TimeSpan FirstSampleWindow = TimeSpan.FromMilliseconds(500);

    public async Task<double?> GetCpuPercentAsync(CancellationToken ct)
    {
        // 已取消的令牌直接抛出，不必先建性能计数器，也让取消语义可测
        ct.ThrowIfCancellationRequested();

        try
        {
            _cpuCounter ??= new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);

            if (!_hasSample)
            {
                // 第一次取值没有意义，等一个短窗口后再取；后续调用直接按上次到本次的间隔算
                _cpuCounter.NextValue();
                await Task.Delay(FirstSampleWindow, ct);
                _hasSample = true;
            }

            var value = _cpuCounter.NextValue();
            return value < 0 ? null : value;
        }
        catch (OperationCanceledException)
        {
            // 停机取消必须向上抛，否则关闭流程会被当成“采集失败”吞掉
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public double GetMemoryUsedPercent()
    {
        var ms = new MEMORY_STATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORY_STATUSEX>() };
        if (!GlobalMemoryStatusEx(ref ms))
            return 0;
        return 100.0 * (ms.ullTotalPhys - ms.ullAvailPhys) / ms.ullTotalPhys;
    }

    public void Dispose() => _cpuCounter?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_STATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORY_STATUSEX lpBuffer);
}
