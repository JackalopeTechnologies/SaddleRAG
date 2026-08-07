// DirectoryScanJobTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Scanning;
using SaddleRAG.Mcp.Tools;

namespace SaddleRAG.Tests.Models;

public sealed class DirectoryScanJobTests
{
    [Fact]
    public void DirectoryScanIsCancellable()
    {
        Assert.True(JobType.DirectoryScan.IsCancellable());
    }

    [Fact]
    public async Task QueueCapturesLocalDateOnceAndPersistsRelativePathProgress()
    {
        JobFixture fixture = MakeFixture();

        DirectoryScanQueueResult queued = await fixture.Runner.QueueAsync(LibraryId,
                                                                           profile: null,
                                                                           TestContext.Current.CancellationToken);
        JobRecord completed = await fixture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5),
                                                                     TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanQueueStatuses.Queued, queued.Status);
        Assert.Equal(Version, queued.Version);
        Assert.Equal(JobType.DirectoryScan, completed.JobType);
        Assert.Equal(Version, completed.Version);
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Equal("documents", completed.ItemsLabel);
        Assert.Equal(2, completed.ItemsProcessed);
        Assert.Equal(4, completed.ItemsTotal);
        Assert.NotNull(completed.DirectoryScanProgress);
        Assert.Equal(5, completed.DirectoryScanProgress.FilesDiscovered);
        Assert.Equal(4, completed.DirectoryScanProgress.SupportedDocuments);
        Assert.Equal(2, completed.DirectoryScanProgress.DocumentsCompleted);
        Assert.Equal("nested/manual.pdf", completed.DirectoryScanProgress.CurrentRelativePath);
        Assert.DoesNotContain(RootPath,
                              completed.DirectoryScanProgress.CurrentRelativePath,
                              StringComparison.OrdinalIgnoreCase);
        await fixture.Coordinator.Received(requiredNumberOfCalls: 1)
                     .RunAsync(Arg.Is<DirectoryIngestionRequest>(request => request != null
                                                                          && request.Version == Version
                                                                          && request.QueuedAt == QueuedAt
                                                                          && request.LibraryId == LibraryId),
                               Arg.Any<Action<DirectoryScanProgress>?>(),
                               Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnifiedJobStatusIncludesDirectoryScanProgress()
    {
        JobFixture fixture = MakeFixture();
        await fixture.Runner.QueueAsync(LibraryId, profile: null, TestContext.Current.CancellationToken);
        JobRecord completed = await fixture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5),
                                                                     TestContext.Current.CancellationToken);
        fixture.Jobs.GetAsync(completed.Id, Arg.Any<CancellationToken>()).Returns(completed);

        string json = await BackgroundJobTools.GetJobStatus(fixture.Factory,
                                                             completed.Id,
                                                             profile: null,
                                                             TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(nameof(JobType.DirectoryScan), root["JobType"]!.GetValue<string>());
        var progress = root["DirectoryScanProgress"]!.AsObject();
        Assert.Equal(5, progress["FilesDiscovered"]!.GetValue<int>());
        Assert.Equal(4, progress["SupportedDocuments"]!.GetValue<int>());
        Assert.Equal(2, progress["DocumentsCompleted"]!.GetValue<int>());
        Assert.Equal("nested/manual.pdf", progress["CurrentRelativePath"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConstructingRunnerAndLoadingRegistrationDoesNotStartAScan()
    {
        JobFixture fixture = MakeFixture();

        DirectoryLibraryDefinition? definition = await fixture.Sources.GetDirectoryDefinitionAsync(
                                                    LibraryId,
                                                    TestContext.Current.CancellationToken);
        await Task.Yield();

        Assert.NotNull(definition);
        await fixture.Coordinator.DidNotReceiveWithAnyArgs()
                     .RunAsync(default!, default, TestContext.Current.CancellationToken);
        await fixture.Jobs.DidNotReceiveWithAnyArgs()
                     .UpsertAsync(default!, TestContext.Current.CancellationToken);
    }

    private static JobFixture MakeFixture()
    {
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var jobs = Substitute.For<IJobRepository>();
        var sources = Substitute.For<ISourceDocumentRepository>();
        var coordinator = Substitute.For<IDirectoryIngestionCoordinator>();
        var preflight = Substitute.For<IDirectoryDocumentCapabilityPreflight>();
        var cancellation = Substitute.For<IJobCancellationRegistry>();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Returns(CancellationToken.None);
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        lifetime.ApplicationStopped.Returns(CancellationToken.None);
        factory.GetJobRepository(Arg.Any<string?>()).Returns(jobs);
        factory.GetSourceDocumentRepository(Arg.Any<string?>()).Returns(sources);
        sources.GetDirectoryDefinitionAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(Definition());
        preflight.EvaluateAsync(Arg.Any<DirectoryLibraryDefinition>(), Arg.Any<CancellationToken>())
                 .Returns(DocumentCapabilityPreflightResult.PermitWithoutDocling());
        var completed = new TaskCompletionSource<JobRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.UpsertAsync(Arg.Do<JobRecord>(record =>
                                              {
                                                  if (record.Status == JobStatus.Completed)
                                                      completed.TrySetResult(Clone(record));
                                              }),
                         Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        coordinator.RunAsync(Arg.Any<DirectoryIngestionRequest>(),
                             Arg.Any<Action<DirectoryScanProgress>?>(),
                             Arg.Any<CancellationToken>())
                   .Returns(call =>
                            {
                                Action<DirectoryScanProgress>? progress =
                                    call.Arg<Action<DirectoryScanProgress>?>();
                                progress?.Invoke(new DirectoryScanProgress(FilesDiscovered: 5,
                                                                           SupportedDocuments: 4,
                                                                           DocumentsCompleted: 2,
                                                                           CurrentRelativePath:
                                                                           "nested/manual.pdf"));
                                return Task.FromResult(new DirectoryIngestionResult(
                                                           DirectoryIngestionStatuses.Completed,
                                                           LibraryId,
                                                           Version,
                                                           DocumentsProcessed: 4,
                                                           PagesIndexed: 6,
                                                           ChunksIndexed: 8));
                            });
        var clock = new FixedQueueTimeProvider();
        var runner = new DirectoryScanJobRunner(coordinator,
                                                new DirectoryScanVersionProvider(clock),
                                                 factory,
                                                 cancellation,
                                                 lifetime,
                                                 preflight,
                                                 NullLogger<DirectoryScanJobRunner>.Instance);
        return new JobFixture(runner, coordinator, factory, jobs, sources, completed);
    }

    private static DirectoryLibraryDefinition Definition() => new()
        {
            Id = LibraryId,
            RootPath = RootPath,
            Recursive = true,
            AllowedExtensions = DirectoryScanLimits.SupportedExtensions,
            ExclusionPatterns = [],
            BindingStatus = DirectoryLibraryBindingStatus.Bound,
            RegisteredAtUtc = QueuedAt.UtcDateTime
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
            CompletedAt = source.CompletedAt,
            LastProgressAt = source.LastProgressAt,
            CancelledAt = source.CancelledAt,
            DirectoryScanProgress = source.DirectoryScanProgress
        };

    private sealed class FixedQueueTimeProvider : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Stage6Mountain",
                                                                                        TimeSpan.FromHours(-6),
                                                                                        "Stage 6 Mountain",
                                                                                        "Stage 6 Mountain");

        public override DateTimeOffset GetUtcNow() => QueuedAt.ToUniversalTime();
    }

    private sealed record JobFixture(DirectoryScanJobRunner Runner,
                                     IDirectoryIngestionCoordinator Coordinator,
                                     RepositoryFactory Factory,
                                     IJobRepository Jobs,
                                     ISourceDocumentRepository Sources,
                                     TaskCompletionSource<JobRecord> Completed);

    private static readonly DateTimeOffset QueuedAt = new(2026, 8, 4, 23, 50, 0, TimeSpan.FromHours(-6));
    private const string RootPath = "C:\\owned-manuals";
    private const string LibraryId = "manual-library";
    private const string Version = "2026-08-04";
}
