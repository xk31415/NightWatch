using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NightWatch.Heartbeat;
using NightWatch.Models;
using Xunit;

namespace NightWatch.Tests;

public class HeartbeatSecurityTests
{
    [Theory]
    [InlineData("localhost", null, false)]
    [InlineData("LOCALHOST", "", false)]
    [InlineData("127.0.0.1", null, false)]
    [InlineData("::1", "  ", false)]
    [InlineData("[::1]", null, false)]
    [InlineData("0.0.0.0", null, true)]
    [InlineData("0.0.0.0", "  ", true)]
    [InlineData("*", null, true)]
    [InlineData("+", null, true)]
    [InlineData("192.168.1.10", "", true)]
    [InlineData("0.0.0.0", "token", false)]
    public void RequiresToken_OnlyForNonLoopbackHost(string host, string? token, bool expected) =>
        Assert.Equal(expected, HeartbeatApiService.RequiresToken(host, token));

    [Theory]
    [InlineData("MyApp", true)]
    [InlineData("user-A_1", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a/b", false)]
    [InlineData("a\tb", false)]
    public void SegmentValidation(string segment, bool expected) =>
        Assert.Equal(expected, HeartbeatApiService.IsValidSegment(segment));

    [Fact]
    public void SegmentValidation_LengthBoundary()
    {
        Assert.True(HeartbeatApiService.IsValidSegment(new string('a', HeartbeatApiService.MaxSegmentLength)));
        Assert.False(HeartbeatApiService.IsValidSegment(new string('a', HeartbeatApiService.MaxSegmentLength + 1)));
    }

    /// <summary>
    /// 回归：对外监听 + 无 Token 时必须拒绝启动（降级），而不是裸奔或让宿主启动失败。
    /// </summary>
    [Fact]
    public async Task NonLoopbackWithoutToken_DegradesWithoutListening()
    {
        var options = new HttpHeartbeatOptions
        {
            Enabled = true,
            Host = "0.0.0.0",
            Port = FreePort(),
            Token = null
        };
        var api = new HeartbeatApiService(new HeartbeatRegistry(), options, NullLogger<HeartbeatApiService>.Instance);

        await api.StartAsync(CancellationToken.None);

        Assert.False(api.IsStarted);
        await api.StopAsync(CancellationToken.None);
        await api.DisposeAsync();
    }

    [Fact]
    public void Registry_RejectsNewKeysAtCapacity_ButRefreshesExisting()
    {
        var registry = new HeartbeatRegistry();

        Assert.True(registry.Touch("P1", "u1", maxKeys: 1));
        Assert.False(registry.Touch("P2", "u2", maxKeys: 1));
        Assert.True(registry.Touch("P1", "u1", maxKeys: 1));

        var entry = Assert.Single(registry.Snapshot().Values);
        Assert.Equal(2, entry.Count);
    }

    [Fact]
    public async Task Api_TooLongSegment_Is400_AndRegistryFull_Is429()
    {
        var port = FreePort();
        var registry = new HeartbeatRegistry();
        var options = new HttpHeartbeatOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = port,
            Token = "t",
            MaxKeys = 1
        };
        var api = new HeartbeatApiService(registry, options, NullLogger<HeartbeatApiService>.Instance);
        await api.StartAsync(CancellationToken.None);
        Assert.True(api.IsStarted);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var baseUri = $"http://127.0.0.1:{port}";

        try
        {
            var tooLong = new string('a', HeartbeatApiService.MaxSegmentLength + 1);
            var bad = await http.PostAsync($"{baseUri}/heartbeat/{tooLong}/u?token=t", content: null);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

            var first = await http.PostAsync($"{baseUri}/heartbeat/P1/u1?token=t", content: null);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            var second = await http.PostAsync($"{baseUri}/heartbeat/P2/u2?token=t", content: null);
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);

            // 已有键在满容量时仍可刷新
            var refresh = await http.PostAsync($"{baseUri}/heartbeat/P1/u1?token=t", content: null);
            Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

            var state = await http.GetAsync($"{baseUri}/heartbeats?token=t");
            Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        }
        finally
        {
            await api.StopAsync(CancellationToken.None);
            await api.DisposeAsync();
        }
    }



    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
