using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NightWatch.Platform;

/// <summary>Windows 资源采集：PerformanceCounter 读 CPU，GlobalMemoryStatusEx 读内存。</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemMetrics : ISystemMetrics
{
    private PerformanceCounter? _cpuCounter;

    public async Task<double?> GetCpuPercentAsync(CancellationToken ct)
    {
        try
        {
            _cpuCounter ??= new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            _cpuCounter.NextValue(); // 首次调用返回 0，丢弃
            await Task.Delay(500, ct);
            var v = _cpuCounter.NextValue();
            return v < 0 ? null : v;
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
