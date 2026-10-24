// UrlCorrectionToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class UrlCorrectionToolsTests
{
    private sealed record TestHarness(
        RepositoryFactory Factory,
        ILibraryRepository LibraryRepo,
        ScrapeJobRunner Runner,
        IChunkRepository ChunkRepo,
        IPageRepository PageRepo,
        ILibraryProfileRepository ProfileRepo,
        ILibraryIndexRepository IndexRepo,
        IBm25ShardRepository Bm25Repo,
        IJobRepository JobRepo,
        IScrapeAuditRepository AuditRepo,
        ISourceDocumentRepository Sources,
        ILibraryIngestionModeRepository Modes,
        ILibraryIngestionModeLeaseManager LeaseManager,
        ILibraryIngestionModeLease Lease,
        JobCancellationService Cancellation);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ForcedScrapeRefusesPublishedVersionWithoutDeletingAuditOrQueueing(bool resume, bool suspect)
    {
        var harness = MakeFactory();
        harness.LibraryRepo.GetVersionAsync("foo", "1.0", Arg.Any<CancellationToken>())
               .Returns(PublishedVersion() with { Suspect = suspect });
        harness.JobRepo.ListRecentAsync(JobType.Scrape, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([PriorScrapeJob()]);

        string json = await ScrapeDocsTools.ScrapeDocs(harness.Runner,
                                                       harness.Factory,
                                                       NullLogger<ScrapeDocsTools.ScrapeDocsToolsLog>.Instance,
                                                       url: "https://docs.foo.com",
                                                       library: "foo",
                                                       version: "1.0",
                                                       force: true,
                                                       resume: resume,
                                                       ct: TestContext.Current.CancellationToken);

        AssertPublishedRefusal(json);
        await AssertNoDestructiveWorkAsync(harness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingScrapeHistoryExplainsPublishedVersionProtection(bool resume)
    {
        var harness = MakeFactory();
        harness.LibraryRepo.GetVersionAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(PublishedVersion());
        harness.JobRepo.ListRecentAsync(JobType.Scrape, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        string json = resume
                          ? await ScrapeDocsTools.ScrapeDocs(harness.Runner, harness.Factory,
                              NullLogger<ScrapeDocsTools.ScrapeDocsToolsLog>.Instance,
                              library: "foo", version: "1.0", force: true, resume: true,
                              ct: TestContext.Current.CancellationToken)
                          : await RescrapeTools.RescrapeLibrary(harness.Runner, harness.Factory, "foo", "1.0",
                              force: true, ct: TestContext.Current.CancellationToken);
        JsonObject response = Assert.IsType<JsonObject>(JsonNode.Parse(json));

        Assert.Equal("NoPriorJob", response["Status"]?.GetValue<string>());
        Assert.Contains("new version identifier", response["Message"]?.GetValue<string>());
        Assert.Contains("force cannot overwrite", response["Message"]?.GetValue<string>());
        await AssertNoDestructiveWorkAsync(harness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RescrapeRefusesPublishedVersionWithoutDeletingAuditOrQueueing(bool force)
    {
        var harness = MakeFactory();
        harness.LibraryRepo.GetVersionAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(PublishedVersion());
        harness.JobRepo.ListRecentAsync(JobType.Scrape, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([PriorScrapeJob()]);

        string json = await RescrapeTools.RescrapeLibrary(harness.Runner,
                                                          harness.Factory,
                                                          "foo",
                                                          "1.0",
                                                          force: force,
                                                          ct: TestContext.Current.CancellationToken);

        AssertPublishedRefusal(json);
        await AssertNoDestructiveWorkAsync(harness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UrlCorrectionRefusesPublishedVersionBeforeBackgroundQueue(bool dryRun)
    {
        var harness = MakeFactory();
        harness.LibraryRepo.GetVersionAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(PublishedVersion());
        IBackgroundJobRunner background = MakeNoopBackgroundRunner();

        string json = await UrlCorrectionTools.SubmitUrlCorrection(harness.Factory,
                                                                   harness.Runner,
                                                                   background,
                                                                   harness.Cancellation,
                                                                   harness.LeaseManager,
                                                                   "foo", "1.0", "https://docs.foo.com",
                                                                   dryRun: dryRun,
                                                                   ct: TestContext.Current.CancellationToken);

        AssertPublishedRefusal(json);
        Assert.Empty(background.ReceivedCalls());
        await AssertNoDestructiveWorkAsync(harness);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task UrlCorrectionRechecksPublicationInsideBackgroundWork(int publishOnRead)
    {
        var harness = MakeFactory();
        int reads = 0;
        harness.LibraryRepo.GetVersionAsync("foo", "1.0", Arg.Any<CancellationToken>())
               .Returns(_ => ++reads >= publishOnRead ? PublishedVersion() : null);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UrlCorrectionTools.SubmitUrlCorrection(harness.Factory,
                                                    harness.Runner,
                                                    MakeInlineBackgroundRunner(),
                                                    harness.Cancellation,
                                                    harness.LeaseManager,
                                                    "foo", "1.0", "https://docs.foo.com",
                                                    ct: TestContext.Current.CancellationToken));

        Assert.Contains("cannot overwrite a published version", exception.Message, StringComparison.Ordinal);
        await AssertNoDestructiveWorkAsync(harness);
    }

    private static void AssertPublishedRefusal(string json)
    {
        JsonObject result = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        Assert.Equal("Refused", result["Status"]?.GetValue<string>());
        Assert.Equal("PUBLISHED_VERSION", result["Reason"]?.GetValue<string>());
        Assert.Null(result["JobId"]);
    }

    private static async Task AssertNoDestructiveWorkAsync(TestHarness harness)
    {
        await harness.Runner.DidNotReceive().QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await harness.AuditRepo.DidNotReceive().DeleteByLibraryVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.ChunkRepo.DidNotReceive().DeleteChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.PageRepo.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.ProfileRepo.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.IndexRepo.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Bm25Repo.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.LibraryRepo.DidNotReceive().ClearSuspectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Cancellation.DidNotReceive().CancelAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private static LibraryVersionRecord PublishedVersion() => new()
    {
        Id = "foo/1.0",
        LibraryId = "foo",
        Version = "1.0",
        ScrapedAt = DateTime.UtcNow,
        PageCount = 1,
        ChunkCount = 1,
        EmbeddingProviderId = "test",
        EmbeddingModelName = "test",
        EmbeddingDimensions = 1,
        PublicationState = VersionPublicationState.Published
    };

    private static JobRecord PriorScrapeJob() => MakeJobRecord("prior", "foo", "1.0", JobStatus.Completed,
        JsonSerializer.Serialize(new ScrapeJob
        {
            LibraryId = "foo",
            Version = "1.0",
            RootUrl = "https://docs.foo.com",
            LibraryHint = "Foo",
            AllowedUrlPatterns = []
        }));

    [Theory]
    [InlineData("directory", null)]
    [InlineData("directory", "team-profile")]
    [InlineData("directory-evidence", null)]
    [InlineData("directory-evidence", "team-profile")]
    [InlineData("documents", null)]
    [InlineData("documents", "team-profile")]
    [InlineData("children", null)]
    [InlineData("children", "team-profile")]
    public async Task UrlCorrectionRefusesProtectedDataAfterAcquiringWebReservation(string protection, string? profile)
    {
        var harness = MakeFactory();
        harness.Lease.OwnershipStateAtAcquisition.Returns(LibraryIngestionOwnershipState.Reserved);
        if (protection == "directory")
            harness.Sources.GetDirectoryDefinitionAsync("foo", Arg.Any<CancellationToken>()).Returns(new DirectoryLibraryDefinition
            {
                Id = "foo", RootPath = "E:/isolated-directory-fixture", RegisteredAtUtc = DateTime.UtcNow
            });
        harness.Modes.GetLibraryDataEvidenceAsync("foo", Arg.Any<CancellationToken>()).Returns(
            new LibraryIngestionDataEvidence(false, protection == "directory-evidence",
                protection == "documents", protection == "children", false));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UrlCorrectionTools.SubmitUrlCorrection(harness.Factory, harness.Runner, MakeInlineBackgroundRunner(),
                harness.Cancellation, harness.LeaseManager, "foo", "1.0", "https://docs.foo.com",
                profile: profile, ct: TestContext.Current.CancellationToken));

        Assert.Contains("cannot be mutated by web ingestion", failure.Message, StringComparison.Ordinal);
        await AssertNoDestructiveWorkAsync(harness);
        harness.Factory.Received().GetSourceDocumentRepository(profile);
        harness.Factory.DidNotReceive().GetSourceDocumentRepository(Arg.Is<string?>(value => value != profile));
        harness.Factory.DidNotReceive().GetLibraryIngestionModeRepository(Arg.Is<string?>(value => value != profile));
        await harness.Lease.DidNotReceive().TryCommitAsync(Arg.Any<CancellationToken>());
        await harness.Lease.Received(1).DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UrlCorrectionAllowsReconnaissanceOnlyAndEstablishedWebData(bool hasAcquiredContent)
    {
        var harness = MakeFactory();
        harness.Lease.OwnershipStateAtAcquisition.Returns(LibraryIngestionOwnershipState.Reserved);
        harness.Modes.GetLibraryDataEvidenceAsync("foo", Arg.Any<CancellationToken>()).Returns(
            new LibraryIngestionDataEvidence(hasAcquiredContent, false, false, hasAcquiredContent, false)
            {
                HasLibraryProfileData = true
            });

        await UrlCorrectionTools.SubmitUrlCorrection(harness.Factory, harness.Runner, MakeInlineBackgroundRunner(),
            harness.Cancellation, harness.LeaseManager, "foo", "1.0", "https://docs.foo.com",
            profile: "team-profile", ct: TestContext.Current.CancellationToken);

        await harness.Modes.Received(1).GetLibraryDataEvidenceAsync("foo", Arg.Any<CancellationToken>());
        await harness.Lease.Received(1).TryCommitAsync(Arg.Any<CancellationToken>());
        await harness.ChunkRepo.Received(1).DeleteChunksAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await harness.Runner.Received(1).QueueAsync(Arg.Any<ScrapeJob>(), "team-profile", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("lost")]
    [InlineData("cancelled")]
    [InlineData("commit-lost")]
    public async Task UrlCorrectionRefusesDeletionWhenItCannotHoldLibraryOwnership(string condition)
    {
        var harness = MakeFactory();
        using var ownershipLost = new CancellationTokenSource();
        if (condition == "busy")
            harness.LeaseManager.TryAcquireAsync(Arg.Any<string?>(), "foo", LibraryIngestionMode.Web, Arg.Any<CancellationToken>())
                   .Returns((ILibraryIngestionModeLease?)null);
        if (condition == "lost")
            harness.Lease.TryRenewAsync(Arg.Any<CancellationToken>()).Returns(false);
        if (condition == "commit-lost")
            harness.Lease.TryCommitAsync(Arg.Any<CancellationToken>()).Returns(false);
        if (condition == "cancelled")
        {
            await ownershipLost.CancelAsync();
            harness.Lease.OwnershipLostToken.Returns(ownershipLost.Token);
        }

        Exception? failure = await Record.ExceptionAsync(() => UrlCorrectionTools.SubmitUrlCorrection(harness.Factory,
            harness.Runner, MakeInlineBackgroundRunner(), harness.Cancellation, harness.LeaseManager,
            "foo", "1.0", "https://docs.foo.com", ct: TestContext.Current.CancellationToken));

        Assert.NotNull(failure);
        Assert.True(failure is InvalidOperationException or OperationCanceledException);
        await AssertNoDestructiveWorkAsync(harness);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("team-profile")]
    public async Task UrlCorrectionReleasesProfileScopedLeaseBeforeQueueing(string? profile)
    {
        var harness = MakeFactory();
        bool disposed = false;
        harness.Lease.DisposeAsync().Returns(_ =>
                                             {
                                                 disposed = true;
                                                 return ValueTask.CompletedTask;
                                             });
        harness.Runner.QueueAsync(Arg.Any<ScrapeJob>(), profile, Arg.Any<CancellationToken>())
               .Returns(_ =>
                        {
                            Assert.True(disposed);
                            return "new-job-id";
                        });

        await UrlCorrectionTools.SubmitUrlCorrection(harness.Factory, harness.Runner,
            MakeInlineBackgroundRunner(), harness.Cancellation, harness.LeaseManager,
            "foo", "1.0", "https://docs.foo.com", profile: profile, ct: TestContext.Current.CancellationToken);

        await harness.LeaseManager.Received(1).TryAcquireAsync(profile, "foo", LibraryIngestionMode.Web, Arg.Any<CancellationToken>());
        await harness.Lease.Received(1).TryCommitAsync(Arg.Any<CancellationToken>());
        await harness.Runner.Received(1).QueueAsync(Arg.Any<ScrapeJob>(), profile, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitUrlCorrectionDryRunReportsCascadeWithoutWriting()
    {
        var harness = MakeFactory();
        harness.ChunkRepo.GetChunkCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(returnThis: 50);
        harness.PageRepo.GetPageCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(returnThis: 20);

        var json = await UrlCorrectionTools.SubmitUrlCorrection(harness.Factory,
                                                                harness.Runner,
                                                                MakeNoopBackgroundRunner(),
                                                                harness.Cancellation,
                                                                harness.LeaseManager,
                                                                "foo",
                                                                "1.0",
                                                                "https://docs.foo.com",
                                                                dryRun: true,
                                                                profile: null,
                                                                TestContext.Current.CancellationToken
                                                               );

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains("\"Chunks\": 50", json);
        Assert.Contains("\"Pages\": 20", json);
        await harness.Runner.DidNotReceive()
                     .QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await harness.Cancellation.DidNotReceive()
                     .CancelAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitUrlCorrectionDryRunListsActiveJobsAsWouldCancel()
    {
        var harness = MakeFactory();
        var orphan = MakeJobRecord("orphan-1", "foo", "1.0", JobStatus.Running);
        harness.JobRepo.ListActiveAsync("foo", "1.0", JobType.Scrape, Arg.Any<CancellationToken>())
               .Returns([orphan]);

        var json = await UrlCorrectionTools.SubmitUrlCorrection(harness.Factory,
                                                                harness.Runner,
                                                                MakeNoopBackgroundRunner(),
                                                                harness.Cancellation,
                                                                harness.LeaseManager,
                                                                "foo",
                                                                "1.0",
                                                                "https://docs.foo.com",
                                                                dryRun: true,
                                                                profile: null,
                                                                TestContext.Current.CancellationToken
                                                               );

        Assert.Contains("\"WouldCancel\":", json);
        Assert.Contains("\"orphan-1\"", json);
        await harness.Cancellation.DidNotReceive()
                     .CancelAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitUrlCorrectionApplyDropsAndRequeues()
    {
        var harness = MakeFactory();
        harness.ChunkRepo.DeleteChunksAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(returnThis: 50L);
        harness.PageRepo.DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(returnThis: 20L);
        harness.Runner.QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
               .Returns("new-job-id");

        var json = await UrlCorrectionTools.SubmitUrlCorrection(harness.Factory,
                                                                harness.Runner,
                                                                MakeInlineBackgroundRunner(),
                                                                harness.Cancellation,
                                                                harness.LeaseManager,
                                                                "foo",
                                                                "1.0",
                                                                "https://docs.foo.com",
                                                                dryRun: false,
                                                                profile: null,
                                                                TestContext.Current.CancellationToken
                                                               );

        Assert.Contains("\"JobId\":", json);
        Assert.Contains("\"Status\": \"Queued\"", json);
        await harness.ChunkRepo.Received(requiredNumberOfCalls: 1)
                     .DeleteChunksAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await harness.PageRepo.Received(requiredNumberOfCalls: 1)
                     .DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await harness.ProfileRepo.Received(requiredNumberOfCalls: 1)
                     .DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await harness.IndexRepo.Received(requiredNumberOfCalls: 1)
                     .DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await harness.Bm25Repo.Received(requiredNumberOfCalls: 1)
                     .DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await harness.LibraryRepo.Received(requiredNumberOfCalls: 1)
                     .ClearSuspectAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await harness.Runner.Received(requiredNumberOfCalls: 1)
                     .QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitUrlCorrectionApplyCancelsParallelRunningJobsBeforeRequeue()
    {
        var harness = MakeFactory();
        var first = MakeJobRecord("running-1", "foo", "1.0", JobStatus.Running);
        var second = MakeJobRecord("running-2", "foo", "1.0", JobStatus.Running);
        harness.JobRepo.ListActiveAsync("foo", "1.0", JobType.Scrape, Arg.Any<CancellationToken>())
               .Returns([first, second]);
        harness.Cancellation.CancelAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
               .Returns(CancelScrapeOutcome.Signalled);
        harness.Runner.QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
               .Returns("new-job-id");

        var json = await UrlCorrectionTools.SubmitUrlCorrection(harness.Factory,
                                                                harness.Runner,
                                                                MakeInlineBackgroundRunner(),
                                                                harness.Cancellation,
                                                                harness.LeaseManager,
                                                                "foo",
                                                                "1.0",
                                                                "https://docs.foo.com",
                                                                dryRun: false,
                                                                profile: null,
                                                                TestContext.Current.CancellationToken
                                                               );

        Assert.Contains("\"JobId\":", json);
        Assert.Contains("\"Status\": \"Queued\"", json);
        await harness.Cancellation.Received(requiredNumberOfCalls: 1)
                     .CancelAsync("running-1", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await harness.Cancellation.Received(requiredNumberOfCalls: 1)
                     .CancelAsync("running-2", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private static JobRecord MakeJobRecord(string id, string library, string version, JobStatus status, string? inputJson = null) =>
        new JobRecord
            {
                Id = id,
                JobType = JobType.Scrape,
                LibraryId = library,
                Version = version,
                InputJson = inputJson ?? "{}",
                Status = status,
                CreatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(minutes: 10)
            };

    private static TestHarness MakeFactory()
    {
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var pageRepo = Substitute.For<IPageRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25Repo = Substitute.For<IBm25ShardRepository>();
        var jobRepo = Substitute.For<IJobRepository>();
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var sources = Substitute.For<ISourceDocumentRepository>();
        var modes = Substitute.For<ILibraryIngestionModeRepository>();
        modes.GetLibraryDataEvidenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(new LibraryIngestionDataEvidence(false, false, false, false, false));
        var leaseManager = Substitute.For<ILibraryIngestionModeLeaseManager>();
        var lease = Substitute.For<ILibraryIngestionModeLease>();
        lease.TryRenewAsync(Arg.Any<CancellationToken>()).Returns(true);
        lease.TryCommitAsync(Arg.Any<CancellationToken>()).Returns(true);
        lease.TryReconcileReservedModeAsync(Arg.Any<LibraryIngestionMode>(), Arg.Any<CancellationToken>()).Returns(true);
        lease.OwnershipLostToken.Returns(CancellationToken.None);
        leaseManager.TryAcquireAsync(Arg.Any<string?>(), Arg.Any<string>(), LibraryIngestionMode.Web, Arg.Any<CancellationToken>())
                    .Returns(lease);
        jobRepo.ListActiveAsync(Arg.Any<string>(),
                                Arg.Any<string?>(),
                                Arg.Any<JobType?>(),
                                Arg.Any<CancellationToken>()
                               )
               .Returns([]);
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepo);
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(profileRepo);
        factory.GetLibraryIndexRepository(Arg.Any<string?>()).Returns(indexRepo);
        factory.GetBm25ShardRepository(Arg.Any<string?>()).Returns(bm25Repo);
        factory.GetJobRepository(Arg.Any<string?>()).Returns(jobRepo);
        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(auditRepo);
        factory.GetSourceDocumentRepository(Arg.Any<string?>()).Returns(sources);
        factory.GetLibraryIngestionModeRepository(Arg.Any<string?>()).Returns(modes);

        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        var runner = Substitute.ForPartsOf<ScrapeJobRunner>(null!,
                                                            null!,
                                                            null!,
                                                            null!,
                                                            null!,
                                                            null!,
                                                            new JobCancellationRegistry(),
                                                            lifetime
                                                           );
        runner.QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(string.Empty);

        var cancellation = Substitute.ForPartsOf<JobCancellationService>(
            Substitute.For<IJobCancellationRegistry>(),
            factory,
            Substitute.For<IMonitorBroadcaster>()
        );
        cancellation.CancelAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(CancelScrapeOutcome.Signalled);

        return new TestHarness(factory,
                               libraryRepo,
                               runner,
                               chunkRepo,
                               pageRepo,
                               profileRepo,
                               indexRepo,
                               bm25Repo,
                               jobRepo,
                               auditRepo,
                               sources,
                               modes,
                               leaseManager,
                               lease,
                               cancellation
                              );
    }

    private static IBackgroundJobRunner MakeNoopBackgroundRunner()
    {
        var runner = Substitute.For<IBackgroundJobRunner>();
        runner.QueueAsync(Arg.Any<BackgroundJobRecord>(),
                          Arg.Any<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>(),
                          Arg.Any<CancellationToken>()
                         )
              .Returns(Guid.NewGuid().ToString());
        return runner;
    }

    private static IBackgroundJobRunner MakeInlineBackgroundRunner()
    {
        var runner = Substitute.For<IBackgroundJobRunner>();
        runner.QueueAsync(Arg.Any<BackgroundJobRecord>(),
                          Arg.Any<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>(),
                          Arg.Any<CancellationToken>()
                         )
              .Returns(async callInfo =>
                       {
                           var record = callInfo.Arg<BackgroundJobRecord>()!;
                           var execute = callInfo
                               .Arg<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>()!;
                           await execute(record, arg2: null, CancellationToken.None);
                           return record.Id;
                       }
                      );
        return runner;
    }
}
