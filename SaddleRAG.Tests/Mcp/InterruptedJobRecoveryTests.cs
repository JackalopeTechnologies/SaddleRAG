using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Mcp;

namespace SaddleRAG.Tests.Mcp;

public sealed class InterruptedJobRecoveryTests
{
    [Fact]
    public async Task TerminalWriteFailureRemainsObservableAfterRunnerUnregisters()
    {
        var registry = new JobCancellationRegistry();
        var broadcaster = Substitute.For<IMonitorBroadcaster>();
        await JobTaskObserver.RunAsync(() => Task.FromException(new IOException("Final write failed")),
            "job", "local", registry, broadcaster, NullLogger.Instance);
        Assert.False(registry.IsRegistered("job"));
        InterruptedJob pending = Assert.Single(registry.GetInterrupted());
        Assert.Equal("local", pending.Profile);
        broadcaster.Received(1).RecordJobFailed("job", JobTaskObserver.FailureDetail);
    }

    [Fact]
    public async Task OutageCancelsOnlyAffectedJobsAndSavesFailureAfterRecovery()
    {
        var settings = new SaddleRagDbSettings
        {
            ActiveProfile = "local",
            Profiles = new()
            {
                ["local"] = new MongoDbProfile { ConnectionString = "mongodb://127.0.0.1:1/?appName=" + Guid.NewGuid().ToString("N"), DatabaseName = "health_test" },
                ["other"] = new MongoDbProfile { ConnectionString = "mongodb://127.0.0.1:2/?appName=" + Guid.NewGuid().ToString("N"), DatabaseName = "health_test" }
            }
        };
        var contexts = new SaddleRagDbContextFactory(Options.Create(settings));
        var registry = new JobCancellationRegistry();
        using var affected = new CancellationTokenSource();
        using var other = new CancellationTokenSource();
        registry.Register("affected", affected, "local");
        registry.Register("other", other, "other");
        var repository = Substitute.For<IJobRepository>();
        var record = new JobRecord { Id = "affected", JobType = JobType.Scrape, Status = JobStatus.Running };
        repository.GetAsync("affected", Arg.Any<CancellationToken>()).Returns(record);
        var factories = Substitute.For<RepositoryFactory>(contexts);
        factories.GetJobRepository("local").Returns(repository);
        var broadcaster = Substitute.For<IMonitorBroadcaster>();
        var recovery = new InterruptedJobRecovery(registry, contexts, factories, broadcaster,
            NullLogger<InterruptedJobRecovery>.Instance);
        contexts.GetDefault().Reachability.RecordHealthCheck(false);

        await recovery.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(affected.IsCancellationRequested);
        Assert.False(other.IsCancellationRequested);
        broadcaster.Received().RecordJobFailed("affected", Arg.Any<string>());
        await repository.DidNotReceive().UpsertAsync(Arg.Any<JobRecord>(), Arg.Any<CancellationToken>());

        registry.Unregister("affected");
        contexts.GetDefault().Reachability.RecordHealthCheck(true);
        await recovery.CheckAsync(TestContext.Current.CancellationToken);
        await repository.Received(1).UpsertAsync(Arg.Is<JobRecord>(job =>
            job != null && job.Status == JobStatus.Failed && job.ErrorMessage != null), Arg.Any<CancellationToken>());
        await recovery.CheckAsync(TestContext.Current.CancellationToken);
        await repository.Received(1).UpsertAsync(Arg.Any<JobRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void NamedAndDefaultDatabaseContextsFailFastTogetherAndRecover()
    {
        var settings = new SaddleRagDbSettings
        {
            ActiveProfile = "local",
            Profiles = new() { ["local"] = new MongoDbProfile
                { ConnectionString = "mongodb://127.0.0.1:1/?appName=" + Guid.NewGuid().ToString("N"), DatabaseName = "health_test" } }
        };
        var contexts = new SaddleRagDbContextFactory(Options.Create(settings));
        contexts.GetDefault().Reachability.RecordHealthCheck(false);
        Assert.Throws<DatabaseUnavailableException>(() => contexts.GetForProfile("local").Libraries);
        contexts.GetDefault().Reachability.RecordHealthCheck(true);
        Assert.NotNull(contexts.GetForProfile("local").Libraries);
    }
}
