using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Core;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Documents.Docling;
using SaddleRAG.Ingestion.Embedding;
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

    [Fact]
    public void ProbesReportWhichDependenciesAreHostedOnThisComputer()
    {
        DependencyHealthMonitor monitor = CreateMonitor("local",
                                                        new Dictionary<string, string>
                                                        {
                                                            ["local"] = "mongodb://localhost:27017",
                                                            ["shared"] = "mongodb://db.lan:27017",
                                                            ["cloud"] = "mongodb+srv://cluster.example.net"
                                                        },
                                                        "http://localhost:5001",
                                                        "http://ollama.lan:11434");

        Dictionary<string, bool?> locality = monitor.CreateProbes()
                                                    .ToDictionary(probe => probe.Name, probe => probe.HostedLocally);

        Assert.True(locality["MongoDB (local)"]);
        Assert.False(locality["MongoDB (shared)"]);
        Assert.False(locality["MongoDB (cloud)"]);
        Assert.True(locality["Document conversion"]);
        Assert.False(locality["Ollama"]);
    }

    [Fact]
    public void LoopbackOllamaAndRemoteDoclingAreReportedFromTheirOwnEndpoints()
    {
        DependencyHealthMonitor monitor = CreateMonitor("local",
                                                        new Dictionary<string, string>
                                                        {
                                                            ["local"] = "mongodb://127.0.0.1:27017"
                                                        },
                                                        "http://docling.lan:5001",
                                                        "http://127.0.0.1:11434");

        Dictionary<string, bool?> locality = monitor.CreateProbes()
                                                    .ToDictionary(probe => probe.Name, probe => probe.HostedLocally);

        Assert.True(locality["Ollama"]);
        Assert.False(locality["Document conversion"]);
    }

    [Fact]
    public async Task RunnerRecordsLocalityOnHealthyAndUnavailableObservations()
    {
        var state = new DependencyHealthState();
        bool available = false;
        var probe = new DependencyProbe("test", true, "test work", "Start the service",
            _ => available ? Task.FromResult("Ready") : Task.FromException<string>(new IOException("offline")),
            true);

        await DependencyProbeRunner.CheckAsync(probe, state, TimeSpan.FromSeconds(1),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(state.Snapshot()).HostedLocally);

        available = true;
        await DependencyProbeRunner.CheckAsync(probe, state, TimeSpan.FromSeconds(1),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(state.Snapshot()).HostedLocally);
    }

    [Fact]
    public void RecordKeepsLocalityAcrossAStatusChange()
    {
        var state = new DependencyHealthState();
        state.Record(new DependencyHealth("Ollama", "Healthy", false, "work", "ok", "", DateTimeOffset.UtcNow,
                                          HostedLocally: false));
        bool changed = state.Record(new DependencyHealth("Ollama", "Unavailable", false, "work", "down", "",
                                                         DateTimeOffset.UtcNow, HostedLocally: false));

        Assert.True(changed);
        Assert.False(Assert.Single(state.Snapshot()).HostedLocally);
    }

    [Fact]
    public void HealthJsonOmitsLocalityWhenUnknownAndReportsItWhenKnown()
    {
        var unknown = new DependencyHealth("Service health checks", "Healthy", true, "work", "", "", null);
        DependencyHealth known = unknown with { HostedLocally = true };

        string unknownJson = JsonSerializer.Serialize(unknown, JsonSerializerOptions.Web);
        string knownJson = JsonSerializer.Serialize(known, JsonSerializerOptions.Web);

        Assert.DoesNotContain("hostedLocally", unknownJson, StringComparison.Ordinal);
        Assert.Contains("\"hostedLocally\":true", knownJson, StringComparison.Ordinal);
    }

    private static DependencyHealthMonitor CreateMonitor(string activeProfile,
                                                          Dictionary<string, string> profiles,
                                                          string doclingEndpoint,
                                                          string ollamaEndpoint)
    {
        var dbSettings = new SaddleRagDbSettings { ActiveProfile = activeProfile };
        foreach ((string name, string connectionString) in profiles)
            dbSettings.Profiles[name] = new MongoDbProfile { ConnectionString = connectionString };

        IServiceProvider services = new ServiceCollection()
                                    .AddSingleton(new SaddleRagDbContextFactory(Options.Create(dbSettings)))
                                    .AddSingleton(Options.Create(new OnnxSettings()))
                                    .AddSingleton(Options.Create(new OllamaSettings { Endpoint = ollamaEndpoint }))
                                    .AddSingleton(Options.Create(new DoclingSettings { Endpoint = doclingEndpoint }))
                                    .AddSingleton(Substitute.For<ILlmClassifier>())
                                    .BuildServiceProvider();
        return new DependencyHealthMonitor(services, new DependencyHealthState(),
                                           NullLogger<DependencyHealthMonitor>.Instance);
    }
}
