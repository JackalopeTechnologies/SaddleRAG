// ReembedJobRunnerRecoveryTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Recon;

#endregion

namespace SaddleRAG.Tests.Ingestion;

public sealed class ReembedJobRunnerRecoveryTests
{
    [Fact]
    public async Task QueuePreservesProfileAndDuplicateDispatchClaimsAndExecutesOnce()
    {
        RunnerFixture fixture = MakeFixture();
        var releaseClaim = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Jobs.TryClaimQueuedAsync(Arg.Any<string>(),
                                         JobType.Reembed,
                                         Profile,
                                         Arg.Any<string>(),
                                         Arg.Any<DateTime>(),
                                         Arg.Any<CancellationToken>())
               .Returns(async call =>
                        {
                            await releaseClaim.Task;
                            JobRecord queued = await fixture.Queued.Task;
                            return (JobRecord?)Claim(queued,
                                                     call.ArgAt<string>(position: 3),
                                                     call.ArgAt<DateTime>(position: 4));
                        });

        string jobId = await fixture.Runner.QueueAsync(LibraryId,
                                                        Version,
                                                        new ReembedOptions(),
                                                        Profile,
                                                        TestContext.Current.CancellationToken);
        JobRecord persisted = await fixture.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5),
                                                                   TestContext.Current.CancellationToken);

        Assert.Equal(jobId, persisted.Id);
        Assert.Equal(Profile, persisted.Profile);
        Assert.False(fixture.Runner.TryDispatchPersisted(persisted));
        releaseClaim.TrySetResult(true);

        JobRecord completed = await fixture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5),
                                                                      TestContext.Current.CancellationToken);
        string broadcastJobId = await fixture.BroadcastCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5),
                                                                                  TestContext.Current.CancellationToken);

        Assert.Equal(jobId, broadcastJobId);
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Equal(Profile, completed.Profile);
        Assert.NotNull(completed.ExecutionClaimId);
        await fixture.Jobs.Received(requiredNumberOfCalls: 1)
                     .TryClaimQueuedAsync(jobId,
                                          JobType.Reembed,
                                          Profile,
                                          Arg.Any<string>(),
                                          Arg.Any<DateTime>(),
                                          Arg.Any<CancellationToken>());
        fixture.Factory.Received(requiredNumberOfCalls: 2).GetJobRepository(Profile);
        fixture.Factory.Received(requiredNumberOfCalls: 1).GetChunkRepository(Profile);
        fixture.Factory.Received(requiredNumberOfCalls: 1).GetLibraryRepository(Profile);
        fixture.Broadcaster.Received(requiredNumberOfCalls: 1)
               .RecordJobStarted(jobId, LibraryId, Version, string.Empty);
        fixture.Broadcaster.Received(requiredNumberOfCalls: 1).RecordJobCompleted(jobId, indexedPageCount: 0);
    }

    [Fact]
    public async Task AppliedThenThrowClaimIsConfirmedAndExecutedByClaimOwner()
    {
        RunnerFixture fixture = MakeFixture();
        JobRecord queued = Job("ambiguous-claim");
        JobRecord? durable = null;
        fixture.Jobs.TryClaimQueuedAsync(queued.Id,
                                         JobType.Reembed,
                                         Profile,
                                         Arg.Any<string>(),
                                         Arg.Any<DateTime>(),
                                         Arg.Any<CancellationToken>())
               .Returns(call =>
                        {
                            durable = Claim(queued,
                                            call.ArgAt<string>(position: 3),
                                            call.ArgAt<DateTime>(position: 4));
                            return Task.FromException<JobRecord?>(new IOException("claim acknowledgement lost"));
                        });
        fixture.Jobs.GetAsync(queued.Id, Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromResult(durable));

        Assert.True(fixture.Runner.TryDispatchPersisted(queued));

        JobRecord completed = await fixture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5),
                                                                      TestContext.Current.CancellationToken);
        await fixture.BroadcastCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5),
                                                         TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Completed, completed.Status);
        string durableClaimId = Assert.IsType<string>(durable?.ExecutionClaimId);
        Assert.Equal(durableClaimId, completed.ExecutionClaimId);
        await fixture.Jobs.Received(requiredNumberOfCalls: 1)
                     .GetAsync(queued.Id, CancellationToken.None);
        await fixture.Jobs.Received(requiredNumberOfCalls: 1)
                     .TryClaimQueuedAsync(queued.Id,
                                          JobType.Reembed,
                                          Profile,
                                          Arg.Any<string>(),
                                          Arg.Any<DateTime>(),
                                          Arg.Any<CancellationToken>());
    }

    private static RunnerFixture MakeFixture()
    {
        var embeddingProvider = Substitute.For<IEmbeddingProvider>();
        embeddingProvider.ProviderId.Returns("test-provider");
        embeddingProvider.ModelName.Returns("test-model");
        embeddingProvider.Dimensions.Returns(3);
        var vectorSearch = Substitute.For<IVectorSearchProvider>();
        var chunkRepository = Substitute.For<IChunkRepository>();
        var libraryRepository = Substitute.For<ILibraryRepository>();
        var jobs = Substitute.For<IJobRepository>();
        chunkRepository.GetChunksAsync(LibraryId, Version, Arg.Any<CancellationToken>())
                       .Returns([]);
        libraryRepository.GetVersionAsync(LibraryId, Version, Arg.Any<CancellationToken>())
                         .Returns((LibraryVersionRecord?)null);

        var factory = Substitute.For<RepositoryFactory>([null!]);
        factory.GetJobRepository(Profile).Returns(jobs);
        factory.GetChunkRepository(Profile).Returns(chunkRepository);
        factory.GetLibraryRepository(Profile).Returns(libraryRepository);
        var queued = new TaskCompletionSource<JobRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<JobRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.UpsertAsync(Arg.Do<JobRecord>(record =>
                                              {
                                                  if (record.Status == JobStatus.Queued)
                                                      queued.TrySetResult(Clone(record));
                                                  if (record.Status == JobStatus.Completed)
                                                      completed.TrySetResult(Clone(record));
                                              }),
                         Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var broadcaster = Substitute.For<IMonitorBroadcaster>();
        var broadcastCompleted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        broadcaster.When(instance => instance.RecordJobCompleted(Arg.Any<string>(), Arg.Any<int>()))
                   .Do(call => broadcastCompleted.TrySetResult(call.ArgAt<string>(position: 0)));
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        var service = new ReembedService(embeddingProvider,
                                         vectorSearch,
                                         NullLogger<ReembedService>.Instance);
        var runner = new ReembedJobRunner(service,
                                          factory,
                                          broadcaster,
                                          Substitute.For<IJobCancellationRegistry>(),
                                          lifetime,
                                          NullLogger<ReembedJobRunner>.Instance);
        return new RunnerFixture(runner,
                                 factory,
                                 jobs,
                                 broadcaster,
                                 queued,
                                 completed,
                                 broadcastCompleted);
    }

    private static JobRecord Claim(JobRecord queued, string claimId, DateTime startedAt) => new()
        {
            Id = queued.Id,
            JobType = queued.JobType,
            Profile = queued.Profile,
            LibraryId = queued.LibraryId,
            Version = queued.Version,
            InputJson = queued.InputJson,
            Status = JobStatus.Running,
            PipelineState = nameof(JobStatus.Running),
            ItemsLabel = queued.ItemsLabel,
            CreatedAt = queued.CreatedAt,
            StartedAt = startedAt,
            ExecutionClaimId = claimId
        };

    private static JobRecord Clone(JobRecord source) => new()
        {
            Id = source.Id,
            JobType = source.JobType,
            Profile = source.Profile,
            LibraryId = source.LibraryId,
            Version = source.Version,
            InputJson = source.InputJson,
            Status = source.Status,
            PipelineState = source.PipelineState,
            ItemsProcessed = source.ItemsProcessed,
            ItemsTotal = source.ItemsTotal,
            ItemsLabel = source.ItemsLabel,
            ResultJson = source.ResultJson,
            ErrorMessage = source.ErrorMessage,
            CreatedAt = source.CreatedAt,
            StartedAt = source.StartedAt,
            ExecutionClaimId = source.ExecutionClaimId,
            CompletedAt = source.CompletedAt,
            LastProgressAt = source.LastProgressAt,
            CancelledAt = source.CancelledAt
        };

    private static JobRecord Job(string id) => new()
        {
            Id = id,
            JobType = JobType.Reembed,
            Profile = Profile,
            LibraryId = LibraryId,
            Version = Version,
            InputJson = "{}",
            Status = JobStatus.Queued,
            ItemsLabel = "chunks"
        };

    private sealed record RunnerFixture(ReembedJobRunner Runner,
                                        RepositoryFactory Factory,
                                        IJobRepository Jobs,
                                        IMonitorBroadcaster Broadcaster,
                                        TaskCompletionSource<JobRecord> Queued,
                                        TaskCompletionSource<JobRecord> Completed,
                                        TaskCompletionSource<string> BroadcastCompleted);

    private const string LibraryId = "recovery-library";
    private const string Profile = "company";
    private const string Version = "1.0";
}
