using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NightWatch.Heartbeat;
using NightWatch.Models;
using Xunit;

namespace NightWatch.Tests;

public class HeartbeatApiDegradeTests
{
    /// <summary>
    /// 回归：端口被占用时 HTTP 接收端必须降级，不能让宿主启动失败——
    /// 否则整个看门狗（进程/服务/资源监控）都起不来。
    /// </summary>
    [Fact]
    public async Task PortInUse_StartDoesNotThrow_AndCanBeDisposed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var options = new HttpHeartbeatOptions
            {
                Enabled = true,
                Host = "127.0.0.1",
                Port = port,
                Token = "token"
            };
            var api = new HeartbeatApiService(new HeartbeatRegistry(), options, NullLogger<HeartbeatApiService>.Instance);

            await api.StartAsync(CancellationToken.None);

            // 未占据端口时能够正常启停；被占用时这里只是降级关闭，不允许抛异常。
            await api.StopAsync(CancellationToken.None);
            await api.DisposeAsync();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Disabled_StartAndStopAreNoOps()
    {
        var api = new HeartbeatApiService(
            new HeartbeatRegistry(),
            new HttpHeartbeatOptions { Enabled = false },
            NullLogger<HeartbeatApiService>.Instance);

        await api.StartAsync(CancellationToken.None);
        await api.StopAsync(CancellationToken.None);
        await api.DisposeAsync();
    }
}
