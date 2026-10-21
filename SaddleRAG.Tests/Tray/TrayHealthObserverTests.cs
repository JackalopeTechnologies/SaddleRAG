using System.Net;
using System.Text;
using SaddleRAG.Tray.Services;

namespace SaddleRAG.Tests.Tray;

public sealed class TrayHealthObserverTests
{
    [Fact]
    public async Task UnavailableResponseShowsFailedServiceInsteadOfGreenRunningIcon()
    {
        using var client = new HttpClient(new ResponseHandler(HttpStatusCode.ServiceUnavailable,
            """{"status":"Unavailable","services":[{"name":"MongoDB (local)","status":"Unavailable"}]}"""));
        var observer = new TrayHealthObserver(client, new Uri("http://localhost:6100"));
        TrayAttention attention = await observer.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(McpServiceState.Stopped, attention.IconState);
        Assert.Contains("MongoDB", attention.Summary);
        Assert.Equal("/monitor/health", attention.TargetPath);
        Assert.True(observer.ShouldNotify(attention));
        Assert.False(observer.ShouldNotify(attention));
    }

    [Fact]
    public async Task UnreadWarningsPersistUntilReadAndNewWarningsNotifyAgain()
    {
        var handler = new ResponseHandler(HttpStatusCode.OK,
            """{"status":"Healthy","notifications":{"warnings":2,"errors":0,"newestAt":"2026-10-21T01:00:00Z","readFailed":false}}""");
        using var client = new HttpClient(handler);
        var observer = new TrayHealthObserver(client, new Uri("http://localhost:6100"));
        TrayAttention attention = await observer.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(McpServiceState.Transitioning, attention.IconState);
        Assert.Equal("/monitor/logs", attention.TargetPath);
        Assert.True(observer.ShouldNotify(attention));
        Assert.False(observer.ShouldNotify(attention));

        handler.Body = """{"status":"Healthy","notifications":{"warnings":0,"errors":0,"newestAt":null,"readFailed":false}}""";
        TrayAttention cleared = await observer.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(McpServiceState.Running, cleared.IconState);
        Assert.False(observer.ShouldNotify(cleared));
        Assert.True(observer.ShouldNotify(attention));
    }

    [Fact]
    public async Task InvalidHealthResponseIsAVisibleFailure()
    {
        using var client = new HttpClient(new ResponseHandler(HttpStatusCode.BadGateway, "<html>not health</html>"));
        var observer = new TrayHealthObserver(client, new Uri("http://localhost:6100"));
        Assert.Equal(TrayAttention.Unreachable, await observer.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ErrorsOutsideTheLogWindowKeepTheTrayWarning()
    {
        using var client = new HttpClient(new ResponseHandler(HttpStatusCode.OK,
            """{"status":"Healthy","notifications":{"warnings":0,"errors":0,"hasErrors":true,"newestAt":"2026-10-21T01:00:00Z","readFailed":false}}"""));
        var observer = new TrayHealthObserver(client, new Uri("http://localhost:6100"));

        TrayAttention attention = await observer.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(McpServiceState.Stopped, attention.IconState);
        Assert.Equal("/monitor/logs", attention.TargetPath);
        Assert.Contains("Unread server errors", attention.Summary);
    }

    private sealed class ResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string Body { get; set; } = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
                { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
    }
}
