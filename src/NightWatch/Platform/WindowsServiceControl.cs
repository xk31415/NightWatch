using System.Runtime.Versioning;
using System.ServiceProcess;

namespace NightWatch.Platform;

/// <summary>Windows 服务控制：封装 <see cref="ServiceController"/>。</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceControl : IServiceControl
{
    public ServiceUnitStatus GetStatus(string serviceName)
    {
        using var sc = new ServiceController(serviceName);
        return Map(sc.Status);
    }

    public void Start(string serviceName)
    {
        using var sc = new ServiceController(serviceName);
        sc.Start();
    }

    public void Continue(string serviceName)
    {
        using var sc = new ServiceController(serviceName);
        sc.Continue();
    }

    public void WaitForRunning(string serviceName, TimeSpan timeout)
    {
        using var sc = new ServiceController(serviceName);
        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    internal static ServiceUnitStatus Map(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Running => ServiceUnitStatus.Running,
        ServiceControllerStatus.Stopped => ServiceUnitStatus.Stopped,
        ServiceControllerStatus.Paused => ServiceUnitStatus.Paused,
        ServiceControllerStatus.StartPending => ServiceUnitStatus.StartPending,
        _ => ServiceUnitStatus.Other
    };
}
