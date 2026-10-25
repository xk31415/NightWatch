using Xunit;
using NightWatch.Models;
using NightWatch.Notification;
using NightWatch.Security;
using Serilog;

namespace NightWatch.Tests;

public class WebhookAndNamesTests
{
    [Theory]
    [InlineData("{\"errcode\":0}", false)]
    [InlineData("{\"errcode\": 0}", false)]
    [InlineData("{\"errcode\":93000}", true)]
    [InlineData("{\"errmsg\":\"ok\"}", true)]
    [InlineData("<html>error</html>", true)]
    public void Webhook_ParsesErrcode(string body, bool error) =>
        Assert.Equal(error, WebhookResponse.IsError(body));

    [Fact]
    public void RuleNames_FillEmpty_AndDisambiguate()
    {
        var options = new WatchDogOptions
        {
            Processes =
            [
                new ProcessRule { Name = "" },
                new ProcessRule { Name = "app" },
                new ProcessRule { Name = "app" }
            ]
        };

        RuleNameGuard.Normalize(options, new LoggerConfiguration().CreateLogger());

        Assert.Equal("unnamed-process-1", options.Processes[0].Name);
        Assert.Equal("app", options.Processes[1].Name);
        Assert.Equal("app#2", options.Processes[2].Name);
    }
}
