using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NightWatch.Heartbeat;
using NightWatch.Models;
using Xunit;

namespace NightWatch.Tests;

public class HeartbeatTokenCompareTests
{
    private const string Expected = "aaaa";

    [Fact]
    public void LengthMismatch_ReturnsFalse_WithoutThrowing()
    {
        Assert.False(HeartbeatApiService.TokensEqual("aa", Expected));
        Assert.False(HeartbeatApiService.TokensEqual("aaaa-extra", Expected));
        // 字符数相同，UTF-8 字节数不同。FixedTimeEquals 在这种输入上会抛异常。
        Assert.False(HeartbeatApiService.TokensEqual("áaaa", Expected));
    }

    [Fact]
    public void SameLength_ComparesContents()
    {
        Assert.False(HeartbeatApiService.TokensEqual("aaab", Expected));
        Assert.True(HeartbeatApiService.TokensEqual(Expected, Expected));
    }
}

public sealed class HeartbeatApiFixture : IAsyncLifetime
{
    public const string Token = "aaaa";
    public int Port { get; private set; }
    private HeartbeatApiService? _api;

    public async Task InitializeAsync()
    {
        Port = FreePort();
        var options = new HttpHeartbeatOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = Port,
            Token = Token
        };
        _api = new HeartbeatApiService(new HeartbeatRegistry(), options, NullLogger<HeartbeatApiService>.Instance);
        await _api.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_api is null)
            return;
        await _api.StopAsync(CancellationToken.None);
        await _api.DisposeAsync();
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

public class HeartbeatTokenAuthTests : IClassFixture<HeartbeatApiFixture>
{
    private readonly HeartbeatApiFixture _fixture;

    public HeartbeatTokenAuthTests(HeartbeatApiFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("aa")]
    [InlineData("aaaa-extra")]
    [InlineData("aaab")]
    [InlineData(null)]
    public async Task WrongOrMissingToken_Is401(string? token)
    {
        var (status, body) = await GetAsync(token);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains("unauthorized", body);
    }

    [Fact]
    public async Task Utf8ByteLengthMismatch_Is401()
    {
        // 请求头只能放 ASCII。查询串会按 UTF-8 解码，覆盖“字符数相同、字节数不同”这条会让 FixedTimeEquals 抛异常的路径。
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var url = $"http://127.0.0.1:{_fixture.Port}/?token={Uri.EscapeDataString("áaaa")}";
        using var response = await http.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("unauthorized", body);
    }

    [Fact]
    public async Task MatchingToken_Is200()
    {
        var (status, body) = await GetAsync(HeartbeatApiFixture.Token);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("running", body);
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string? token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_fixture.Port}/");
        if (token is not null)
            request.Headers.TryAddWithoutValidation("X-NightWatch-Token", token);

        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body);
    }
}
