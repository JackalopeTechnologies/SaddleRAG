using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core;
using SaddleRAG.Core.Models;
using SaddleRAG.Mcp;

namespace SaddleRAG.Tests.Mcp;

public sealed class DependencyHealthTests
{
    [Theory]
    [InlineData(true, "Unavailable", 503)]
    [InlineData(false, "Degraded", 200)]
    public async Task FailedServiceRemainsObservableAndRecovers(bool required, string status, int code)
    {
        var state = new DependencyHealthState();
        var warmup = new McpWarmupState();
        warmup.MarkCompleted("Ready");
        bool available = false;
        var probe = new DependencyProbe("test", required, "test work", "Start the service",
            _ => available ? Task.FromResult("Ready") : Task.FromException<string>(new IOException("offline")));

        await DependencyProbeRunner.CheckAsync(probe, state, TimeSpan.FromSeconds(1),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.Equal(status, ServiceHealthResponseFactory.OverallStatus(warmup, state));
        Assert.Equal(code, ServiceHealthResponseFactory.HttpStatus(status));
        Assert.NotNull(Assert.Single(state.Snapshot()).UnavailableSince);

        available = true;
        await DependencyProbeRunner.CheckAsync(probe, state, TimeSpan.FromSeconds(1),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.Equal("Healthy", ServiceHealthResponseFactory.OverallStatus(warmup, state));
        Assert.Null(Assert.Single(state.Snapshot()).UnavailableSince);
        Assert.NotNull(Assert.Single(state.Snapshot()).LastSucceededAt);
    }

    [Fact]
    public async Task ProbeTimeoutDoesNotStopMonitoringAndShutdownIsNotAServiceFailure()
    {
        var state = new DependencyHealthState();
        var probe = new DependencyProbe("slow", true, "test work", "Check the service",
            async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return "ready"; });

        await DependencyProbeRunner.CheckAsync(probe, state, TimeSpan.FromMilliseconds(10),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.Equal("Unavailable", Assert.Single(state.Snapshot()).Status);
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DependencyProbeRunner.CheckAsync(probe, state, TimeSpan.FromSeconds(1), NullLogger.Instance, shutdown.Token));
        Assert.Single(state.Snapshot());
    }

    [Fact]
    public void FailedStartupCannotReportHealthy()
    {
        var warmup = new McpWarmupState();
        warmup.MarkFailed("Loading", "failure");
        var state = new DependencyHealthState();
        state.Record(new DependencyHealth("MongoDB", "Healthy", true, "Libraries", "Ready", "", DateTimeOffset.UtcNow));
        Assert.Equal("Unavailable", ServiceHealthResponseFactory.OverallStatus(warmup, state));
    }
}
