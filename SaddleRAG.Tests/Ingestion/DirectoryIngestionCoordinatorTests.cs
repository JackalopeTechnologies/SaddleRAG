// DirectoryIngestionCoordinatorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Documents.Intake;
using SaddleRAG.Ingestion.Scanning;

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     Pins the atomic publication boundary around the common Stage 6
///     directory pipeline. IDirectoryIngestionPipeline is the narrow seam
///     implemented by DirectoryPageProducer + IngestionOrchestrator; it
///     returns only after pages, chunks, embeddings, subjects, BM25, and the
///     full vector index are ready.
/// </summary>
public sealed class DirectoryIngestionCoordinatorTests
{
    [Fact]
    public async Task ExplicitRunUsesQueueCapturedLocalDateAndPublishesOnlyAfterThePipelineCompletes()
    {
        CoordinatorFixture fixture = MakeFixture();
        var events = fixture.Events;
        fixture.Pipeline.ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                      Arg.Any<Action<DirectoryScanProgress>?>(),
                                      Arg.Any<CancellationToken>())
                .Returns(call =>
                         {
                             events.Add("pipeline-complete");
                             return PipelineResult();
                         });

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryIngestionStatuses.Completed, result.Status);
        Assert.Equal(Version, result.Version);
        Assert.Equal(4, result.DocumentsProcessed);
        Assert.Equal(6, result.PagesIndexed);
        Assert.Equal(8, result.ChunksIndexed);
        await fixture.Pipeline.Received(requiredNumberOfCalls: 1)
                     .ExecuteAsync(Arg.Is<DirectoryIngestionRequest>(request => request != null
                                                                            && request.Version == Version
                                                                            && request.QueuedAt == QueuedAt
                                                                            && request.ScanRunId == ScanRunId
                                                                            && request.Definition.RootPath == RootPath
                                                                            && request.Definition.RegistrationRevision ==
                                                                            RegistrationRevision),
                                   Arg.Any<Action<DirectoryScanProgress>?>(),
                                   Arg.Any<CancellationToken>());
        AssertOrdered(events,
                      "lease-acquired",
                      "Building",
                      "pipeline-complete",
                      "revisions-published",
                      "catalog-published",
                      "Published",
                      "pointer");
        Assert.Equal(6, fixture.PublicationLease.RenewalCount);
        Assert.Equal(Version, fixture.Library.CurrentVersion);
        Assert.Contains(Version, fixture.Library.AllVersions);
    }

    [Fact]
    public async Task BusyLifecycleLeaseStopsBeforeVersionClaimOrPipelineMutation()
    {
        CoordinatorFixture fixture = MakeFixture();
        fixture.Sources.TryAcquireDirectoryPublicationLeaseAsync(Arg.Any<string>(),
                                                                  Arg.Any<long>(),
                                                                  Arg.Any<string?>(),
                                                                  Arg.Any<string>(),
                                                                  Arg.Any<string?>(),
                                                                  Arg.Any<CancellationToken>())
               .Returns((IDirectoryPublicationLease?)null);

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryIngestionStatuses.Failed, result.Status);
        Assert.Empty(fixture.VersionWrites);
        await fixture.Libraries.DidNotReceive()
                     .TryClaimDirectoryVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                                    Arg.Any<CancellationToken>());
        await fixture.Pipeline.DidNotReceive()
                     .ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                   Arg.Any<Action<DirectoryScanProgress>?>(),
                                   Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BusyModeLeaseStopsBeforeDirectoryOrVersionMutation()
    {
        CoordinatorFixture fixture = MakeFixture(modeLeaseAvailable: false);

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryIngestionStatuses.Failed, result.Status);
        await fixture.Sources.DidNotReceive()
                     .GetDirectoryDefinitionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.Libraries.DidNotReceive()
                     .TryClaimDirectoryVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                                    Arg.Any<CancellationToken>());
        await fixture.Pipeline.DidNotReceive()
                     .ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                   Arg.Any<Action<DirectoryScanProgress>?>(),
                                   Arg.Any<CancellationToken>());
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteScanCandidateUnderLeaseAsync(default,
                                                         default!,
                                                         default!,
                                                         default!,
                                                         default!,
                                                         TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ModeLeaseLossCancelsThePipelineAndPreventsCandidateCleanup()
    {
        CoordinatorFixture fixture = MakeFixture();
        using var ownershipLost = new CancellationTokenSource();
        fixture.ModeLease.OwnershipLostToken.Returns(ownershipLost.Token);
        fixture.Pipeline.ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                      Arg.Any<Action<DirectoryScanProgress>?>(),
                                      Arg.Any<CancellationToken>())
                .Returns(call =>
                         {
                             ownershipLost.Cancel();
                             return Task.FromCanceled<DirectoryIngestionPipelineResult>(
                                 call.Arg<CancellationToken>());
                         });

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryIngestionStatuses.Failed, result.Status);
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteScanCandidateUnderLeaseAsync(default,
                                                         default!,
                                                         default!,
                                                         default!,
                                                         default!,
                                                         TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LaterQueueDateCreatesANewImmutableSnapshotAndRetainsPriorVersion()
    {
        CoordinatorFixture fixture = MakeFixture();
        fixture.Pipeline.ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                      Arg.Any<Action<DirectoryScanProgress>?>(),
                                      Arg.Any<CancellationToken>())
                .Returns(PipelineResult());
        var nextQueuedAt = QueuedAt.AddDays(1);
        var request = Request() with { Version = NextVersion, QueuedAt = nextQueuedAt, ScanRunId = "scan-next" };

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(request,
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryIngestionStatuses.Completed, result.Status);
        Assert.Equal(NextVersion, fixture.Library.CurrentVersion);
        Assert.Equal([PriorVersion, NextVersion], fixture.Library.AllVersions);
        LibraryVersionRecord published = Assert.Single(fixture.VersionWrites,
                                                        record => record.PublicationState ==
                                                                  VersionPublicationState.Published);
        Assert.Equal(PriorVersion, published.PreviousVersion);
        Assert.Equal(nextQueuedAt.UtcDateTime, published.ScrapedAt);
    }

    [Fact]
    public async Task ProgressReportsSupportedDocumentsAndCurrentRelativePathWithoutTheAbsoluteRoot()
    {
        CoordinatorFixture fixture = MakeFixture();
        fixture.Pipeline.ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                      Arg.Any<Action<DirectoryScanProgress>?>(),
                                      Arg.Any<CancellationToken>())
                .Returns(call =>
                         {
                             Action<DirectoryScanProgress>? progress = call.Arg<Action<DirectoryScanProgress>?>();
                             progress?.Invoke(new DirectoryScanProgress(FilesDiscovered: 5,
                                                                        SupportedDocuments: 4,
                                                                        DocumentsCompleted: 2,
                                                                        CurrentRelativePath: "nested/manual.pdf"));
                             return PipelineResult();
                         });
        DirectoryScanProgress? observed = null;

        await fixture.Coordinator.RunAsync(Request(), progress => observed = progress,
                                           TestContext.Current.CancellationToken);

        Assert.NotNull(observed);
        Assert.Equal(5, observed.FilesDiscovered);
        Assert.Equal(4, observed.SupportedDocuments);
        Assert.Equal(2, observed.DocumentsCompleted);
        Assert.Equal("nested/manual.pdf", observed.CurrentRelativePath);
        Assert.DoesNotContain(RootPath, observed.CurrentRelativePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CandidateVersionRecordsCarryThePipelineProcessingManifest()
    {
        CoordinatorFixture fixture = MakeFixture();
        DirectoryIngestionPipelineResult expected = PipelineResult();
        fixture.Pipeline.ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                      Arg.Any<Action<DirectoryScanProgress>?>(),
                                      Arg.Any<CancellationToken>())
                .Returns(expected);

        await fixture.Coordinator.RunAsync(Request(), onProgress: null, TestContext.Current.CancellationToken);

        LibraryVersionRecord published = Assert.Single(fixture.VersionWrites,
                                                        record => record.PublicationState ==
                                                                  VersionPublicationState.Published);
        Assert.Equal(expected.EmbeddingProviderId, published.EmbeddingProviderId);
        Assert.Equal(expected.EmbeddingModelName, published.EmbeddingModelName);
        Assert.Equal(expected.EmbeddingDimensions, published.EmbeddingDimensions);
        Assert.Equal(expected.ClassifierBackend, published.ClassifierBackend);
        Assert.Equal(expected.ClassifierModel, published.ClassifierModel);
        Assert.Equal(expected.SubjectTaxonomyVersion, published.SubjectTaxonomyVersion);
    }

    [Fact]
    public async Task MetadataCasLossDoesNotAdvanceTheCapturedDefinitionPointer()
    {
        CoordinatorFixture fixture = MakeFixture();
        fixture.Pipeline.ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                      Arg.Any<Action<DirectoryScanProgress>?>(),
                                      Arg.Any<CancellationToken>())
                .Returns(PipelineResult());
        fixture.Sources.TryUpdateDirectoryPublicationAsync(fixture.PublicationLease,
                                                           expectedPublishedVersion: null,
                                                           QueuedAt.UtcDateTime,
                                                           Version,
                                                           Arg.Any<CancellationToken>())
               .Returns(false);

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryIngestionStatuses.Failed, result.Status);
        Assert.Equal(PriorVersion, fixture.Library.CurrentVersion);
        await fixture.Sources.DidNotReceive()
                     .UpsertDirectoryDefinitionAsync(Arg.Any<DirectoryLibraryDefinition>(),
                                                     Arg.Any<CancellationToken>());
        await fixture.Sources.Received(requiredNumberOfCalls: 1)
                     .TryUpdateDirectoryPublicationAsync(fixture.PublicationLease,
                                                         expectedPublishedVersion: null,
                                                         QueuedAt.UtcDateTime,
                                                         Version,
                                                         Arg.Any<CancellationToken>());
        await fixture.Libraries.DidNotReceive()
                     .UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MetadataWriteThenThrowRestoresPriorPointerBeforeCandidateCleanup()
    {
        CoordinatorFixture fixture = MakeFixture();
        fixture.Pipeline.ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                      Arg.Any<Action<DirectoryScanProgress>?>(),
                                      Arg.Any<CancellationToken>())
                .Returns(PipelineResult());
        var definitionReads = 0;
        fixture.Sources.GetDirectoryDefinitionAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(_ => ++definitionReads == 1 ? Definition() : LeasedDefinition(ScanRunId));
        fixture.Sources.TryUpdateDirectoryPublicationAsync(fixture.PublicationLease,
                                                            expectedPublishedVersion: null,
                                                            QueuedAt.UtcDateTime,
                                                            Version,
                                                            Arg.Any<CancellationToken>())
               .Returns(_ =>
                        {
                            fixture.Events.Add("metadata-written");
                            fixture.PublicationMetadata.LastPublishedVersion = Version;
                            return Task.FromException<bool>(
                                new IOException("The metadata acknowledgement was lost."));
                        });

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryIngestionStatuses.Failed, result.Status);
        Assert.Null(fixture.PublicationMetadata.LastPublishedVersion);
        Assert.Equal(PriorVersion, fixture.Library.CurrentVersion);
        Assert.Equal(2, definitionReads);
        AssertOrdered(fixture.Events,
                      "metadata-written",
                      "metadata-restored",
                      "catalog-reverted",
                      "candidate-cleaned",
                      "Failed");
        await fixture.Sources.Received(requiredNumberOfCalls: 1)
                     .TryRestoreDirectoryPublicationAsync(fixture.PublicationLease,
                                                          Version,
                                                          restoredPublishedAtUtc: null,
                                                          restoredPublishedVersion: null,
                                                          Arg.Any<CancellationToken>());
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                         LibraryId,
                                                         Version,
                                                         fixture.PublicationLease,
                                                         fixture.ModeLease,
                                                         Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MetadataThrowBeforeWriteConfirmsExactPriorPointerBeforeCandidateCleanup()
    {
        CoordinatorFixture fixture = MakeFixture();
        fixture.Pipeline.ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                      Arg.Any<Action<DirectoryScanProgress>?>(),
                                      Arg.Any<CancellationToken>())
                .Returns(PipelineResult());
        var definitionReads = 0;
        fixture.Sources.GetDirectoryDefinitionAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(_ => ++definitionReads == 1 ? Definition() : LeasedDefinition(ScanRunId));
        fixture.Sources.TryUpdateDirectoryPublicationAsync(fixture.PublicationLease,
                                                            expectedPublishedVersion: null,
                                                            QueuedAt.UtcDateTime,
                                                            Version,
                                                            Arg.Any<CancellationToken>())
               .Returns(_ =>
                        {
                            fixture.Events.Add("metadata-write-threw");
                            return Task.FromException<bool>(
                                new IOException("The metadata write failed before reaching storage."));
                        });
        fixture.Sources.TryRestoreDirectoryPublicationAsync(fixture.PublicationLease,
                                                             Version,
                                                             restoredPublishedAtUtc: null,
                                                             restoredPublishedVersion: null,
                                                             Arg.Any<CancellationToken>())
               .Returns(_ =>
                        {
                            fixture.Events.Add("metadata-restore-missed");
                            return false;
                        });

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryIngestionStatuses.Failed, result.Status);
        Assert.Null(fixture.PublicationMetadata.LastPublishedVersion);
        Assert.Equal(2, definitionReads);
        AssertOrdered(fixture.Events,
                      "metadata-write-threw",
                      "metadata-restore-missed",
                      "catalog-reverted",
                      "candidate-cleaned",
                      "Failed");
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                         LibraryId,
                                                         Version,
                                                         fixture.PublicationLease,
                                                         fixture.ModeLease,
                                                         Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AmbiguousMetadataWithDifferentLeaseOwnerPreservesCandidateAndCleanupFailure()
    {
        CoordinatorFixture fixture = MakeFixture();
        fixture.Pipeline.ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                      Arg.Any<Action<DirectoryScanProgress>?>(),
                                      Arg.Any<CancellationToken>())
                .Returns(PipelineResult());
        var definitionReads = 0;
        fixture.Sources.GetDirectoryDefinitionAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(_ => ++definitionReads == 1 ? Definition() : LeasedDefinition("other-scan"));
        var writeFailure = new IOException("The metadata write outcome is unknown.");
        fixture.Sources.TryUpdateDirectoryPublicationAsync(fixture.PublicationLease,
                                                            expectedPublishedVersion: null,
                                                            QueuedAt.UtcDateTime,
                                                            Version,
                                                            Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<bool>(writeFailure));
        fixture.Sources.TryRestoreDirectoryPublicationAsync(fixture.PublicationLease,
                                                             Version,
                                                             restoredPublishedAtUtc: null,
                                                             restoredPublishedVersion: null,
                                                             Arg.Any<CancellationToken>())
               .Returns(true);

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryIngestionStatuses.Failed, result.Status);
        Assert.Equal(2, definitionReads);
        Assert.True(writeFailure.Data.Contains("DirectoryCandidateCleanupFailure"));
        await fixture.Deletion.DidNotReceive()
                      .DeleteScanCandidateUnderLeaseAsync(Arg.Any<string?>(),
                                                          Arg.Any<string>(),
                                                          Arg.Any<string>(),
                                                          Arg.Any<IDirectoryPublicationLease>(),
                                                          Arg.Any<ILibraryIngestionModeLease>(),
                                                          Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeaseTakeoverAfterCatalogPromotionCleansTheFailedScanAndLeavesPriorPointerActive()
    {
        CoordinatorFixture fixture = MakeFixture(renewalResults: [true, true, true, false]);
        fixture.Pipeline.ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                      Arg.Any<Action<DirectoryScanProgress>?>(),
                                      Arg.Any<CancellationToken>())
                .Returns(PipelineResult());

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryIngestionStatuses.Failed, result.Status);
        Assert.Equal(PriorVersion, fixture.Library.CurrentVersion);
        Assert.DoesNotContain(fixture.VersionWrites,
                              record => record.PublicationState == VersionPublicationState.Published);
        Assert.Equal(4, fixture.PublicationLease.RenewalCount);
        AssertOrdered(fixture.Events, "catalog-published", "catalog-reverted", "candidate-cleaned", "Failed");
        await fixture.Catalogs.Received(requiredNumberOfCalls: 1)
                     .TryRollbackCandidatePublicationAsync(LibraryId,
                                                           PipelineResult().SubjectTaxonomyVersion!,
                                                           ScanRunId,
                                                           Arg.Any<CancellationToken>());
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                         LibraryId,
                                                         Version,
                                                         fixture.PublicationLease,
                                                         fixture.ModeLease,
                                                         Arg.Any<CancellationToken>());
        await fixture.Libraries.DidNotReceive()
                     .UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeaseTakeoverAfterMetadataCasDoesNotAdvanceTheLibraryPointer()
    {
        CoordinatorFixture fixture = MakeFixture(renewalResults: [true, true, true, true, true, false]);
        fixture.Pipeline.ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                      Arg.Any<Action<DirectoryScanProgress>?>(),
                                      Arg.Any<CancellationToken>())
                .Returns(PipelineResult());

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryIngestionStatuses.Failed, result.Status);
        Assert.Equal(PriorVersion, fixture.Library.CurrentVersion);
        Assert.Null(fixture.PublicationMetadata.LastPublishedVersion);
        Assert.Equal(6, fixture.PublicationLease.RenewalCount);
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                         LibraryId,
                                                         Version,
                                                         fixture.PublicationLease,
                                                         fixture.ModeLease,
                                                         Arg.Any<CancellationToken>());
        await fixture.Libraries.DidNotReceive()
                     .UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedMetadataRestorePreservesTheCandidateReferencedByDirectoryMetadata()
    {
        CoordinatorFixture fixture = MakeFixture(renewalResults: [true, true, true, true, true, false]);
        fixture.Pipeline.ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                      Arg.Any<Action<DirectoryScanProgress>?>(),
                                      Arg.Any<CancellationToken>())
                .Returns(PipelineResult());
        fixture.Sources.TryRestoreDirectoryPublicationAsync(fixture.PublicationLease,
                                                             Version,
                                                             restoredPublishedAtUtc: null,
                                                             restoredPublishedVersion: null,
                                                             Arg.Any<CancellationToken>())
               .Returns(false);

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryIngestionStatuses.Failed, result.Status);
        Assert.Equal(PriorVersion, fixture.Library.CurrentVersion);
        Assert.Equal(Version, fixture.PublicationMetadata.LastPublishedVersion);
        Assert.Contains(fixture.VersionWrites,
                        record => record.PublicationState == VersionPublicationState.Published);
        await fixture.Deletion.DidNotReceive()
                      .DeleteScanCandidateUnderLeaseAsync(Arg.Any<string?>(),
                                                          Arg.Any<string>(),
                                                          Arg.Any<string>(),
                                                          Arg.Any<IDirectoryPublicationLease>(),
                                                          Arg.Any<ILibraryIngestionModeLease>(),
                                                          Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedRetryCleanupRerunsTheSameLeaseDeletionBeforeRecordingFailure()
    {
        CoordinatorFixture fixture = MakeFixture();
        fixture.Libraries.TryClaimDirectoryVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                                        Arg.Any<CancellationToken>())
                 .Returns(new DirectoryVersionClaimResult(DirectoryVersionClaimStatus.Acquired,
                                                           RequiresCleanup: true));
        var cleanupAttempts = 0;
        fixture.Deletion.DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                            LibraryId,
                                                            Version,
                                                            fixture.PublicationLease,
                                                            fixture.ModeLease,
                                                            Arg.Any<CancellationToken>())
               .Returns(_ =>
                        {
                            cleanupAttempts++;
                            fixture.Events.Add($"cleanup-{cleanupAttempts}");
                            if (cleanupAttempts == 1)
                                throw new InvalidOperationException("partial candidate cleanup failed");
                            return EmptyDeletionResult;
                        });

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryIngestionStatuses.Failed, result.Status);
        Assert.Equal(2, cleanupAttempts);
        AssertOrdered(fixture.Events, "cleanup-1", "cleanup-2", "Failed");
        await fixture.Deletion.Received(requiredNumberOfCalls: 2)
                     .DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                         LibraryId,
                                                         Version,
                                                         fixture.PublicationLease,
                                                         fixture.ModeLease,
                                                         Arg.Any<CancellationToken>());
        await fixture.Pipeline.DidNotReceive()
                     .ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                   Arg.Any<Action<DirectoryScanProgress>?>(),
                                   Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanEngineCountsFilesAndReportsTheCurrentRelativePathToThePublishingSink()
    {
        var fileSystem = new ScriptedDirectoryScanFileSystem();
        DateTime modified = QueuedAt.UtcDateTime;
        var root = new DirectoryEntrySnapshot(RootPath, FileAttributes.Directory, 0, modified);
        var guide = FileSnapshot("Guide.md", "# Guide\nGuide body", modified);
        var manual = FileSnapshot("Manual.pdf", "%PDF-owned", modified);
        var unsupported = FileSnapshot("image.png", "not supported", modified);
        fileSystem.SetInspection(RootPath, new DirectoryPathResult(root, string.Empty, null));
        fileSystem.SetEnumeration(RootPath,
                                  new DirectoryEnumerationResult([guide.Snapshot,
                                                                  manual.Snapshot,
                                                                  unsupported.Snapshot],
                                                                 string.Empty,
                                                                 null));
        fileSystem.SetRead(guide.Snapshot.FullPath, guide.Read);
        fileSystem.SetRead(manual.Snapshot.FullPath, manual.Read);
        var intake = Substitute.For<IDocumentIntake>();
        intake.ReadAsync(Arg.Any<DocumentIntakeRequest>(), Arg.Any<CancellationToken>())
              .Returns(call => SuccessfulIntake(call.Arg<DocumentIntakeRequest>()!.FileName));
        var sink = Substitute.For<IDirectoryScanSink>();
        var progress = new List<DirectoryScanProgress>();
        var engine = new DirectoryScanEngine(fileSystem,
                                             intake,
                                             NullLogger<DirectoryScanEngine>.Instance,
                                             new FixedEngineTimeProvider());
        var request = new DirectoryScanRequest
                          {
                              LibraryId = LibraryId,
                              RootPath = RootPath,
                              ScanRunId = ScanRunId,
                              Recursive = true,
                              MaxFileBytes = DirectoryScanLimits.DefaultMaxFileBytes
                          };

        DirectoryScanReport report = await engine.ScanAsync(request,
                                                             sink,
                                                             progress.Add,
                                                             TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanStatus.Completed, report.Status);
        Assert.Contains(progress,
                        item => item.FilesDiscovered == 3
                                && item.SupportedDocuments == 2
                                && item.DocumentsCompleted == 1
                                && item.CurrentRelativePath == "guide.md");
        DirectoryScanProgress terminal = progress[^1];
        Assert.Equal(3, terminal.FilesDiscovered);
        Assert.Equal(2, terminal.SupportedDocuments);
        Assert.Equal(2, terminal.DocumentsCompleted);
        Assert.DoesNotContain(progress,
                           item => item.CurrentRelativePath?.Contains(RootPath,
                                                                       StringComparison.OrdinalIgnoreCase) == true);
        await sink.Received(requiredNumberOfCalls: 2)
                  .AcceptAsync(Arg.Any<DirectoryAcquiredDocument>(), Arg.Any<CancellationToken>());
    }

    private static CoordinatorFixture MakeFixture(IReadOnlyList<bool>? renewalResults = null,
                                                  bool modeLeaseAvailable = true)
    {
        var events = new List<string>();
        var versions = new List<LibraryVersionRecord>();
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var libraries = Substitute.For<ILibraryRepository>();
        var sources = Substitute.For<ISourceDocumentRepository>();
        var catalogs = Substitute.For<ISubjectCatalogRepository>();
        var modeLeaseManager = Substitute.For<ILibraryIngestionModeLeaseManager>();
        var modeLease = Substitute.For<ILibraryIngestionModeLease>();
        var pipeline = Substitute.For<IDirectoryIngestionPipeline>();
        var deletion = Substitute.For<ILibraryDeletionService>();
        var publicationMetadata = new PublicationMetadataState();
        var publicationLease = new TestDirectoryPublicationLease(LibraryId,
                                                                  ScanRunId,
                                                                  RegistrationRevision,
                                                                  renewalResults);
        var library = new LibraryRecord
                          {
                              Id = LibraryId,
                              Name = "Manual library",
                              Hint = "manuals",
                              CurrentVersion = PriorVersion,
                              AllVersions = [PriorVersion]
                          };
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraries);
        factory.GetSourceDocumentRepository(Arg.Any<string?>()).Returns(sources);
        factory.GetSubjectCatalogRepository(Arg.Any<string?>()).Returns(catalogs);
        modeLease.OwnershipLostToken.Returns(CancellationToken.None);
        modeLease.TryCommitAsync(Arg.Any<CancellationToken>()).Returns(true);
        modeLeaseManager.TryAcquireAsync(Arg.Any<string?>(),
                                         LibraryId,
                                         LibraryIngestionMode.Directory,
                                         Arg.Any<CancellationToken>())
                        .Returns(modeLeaseAvailable ? modeLease : null);
        sources.GetDirectoryDefinitionAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(Definition());
        libraries.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(library);
        libraries.TryClaimDirectoryVersionAsync(Arg.Do<LibraryVersionRecord>(record =>
                                                      {
                                                          versions.Add(record);
                                                          events.Add(record.PublicationState.ToString());
                                                      }),
                                                Arg.Any<CancellationToken>())
                 .Returns(new DirectoryVersionClaimResult(DirectoryVersionClaimStatus.Acquired));
        libraries.TryPublishDirectoryVersionAsync(Arg.Do<LibraryVersionRecord>(record =>
                                                            {
                                                                versions.Add(record);
                                                                events.Add(record.PublicationState.ToString());
                                                            }),
                                                      Arg.Any<string>(),
                                                      Arg.Any<CancellationToken>())
                 .Returns(true);
        sources.TryAcquireDirectoryPublicationLeaseAsync(Arg.Is<string>(value => value == LibraryId),
                                                           Arg.Is<long>(value => value == RegistrationRevision),
                                                           Arg.Is<string?>(value => value ==
                                                                                         RegistrationIncarnationId),
                                                           Arg.Any<string>(),
                                                           Arg.Is<string?>(value => value == null),
                                                           Arg.Any<CancellationToken>())
               .Returns(_ =>
                        {
                            events.Add("lease-acquired");
                            return publicationLease;
                        });
        sources.PublishCandidateScanRunAsync(LibraryId,
                                             Arg.Any<string>(),
                                             Arg.Any<string>(),
                                             Arg.Any<CancellationToken>())
               .Returns(_ =>
                        {
                            events.Add("revisions-published");
                            return 4L;
                        });
        catalogs.TryPublishCandidateAsync(LibraryId,
                                          PipelineResult().SubjectTaxonomyVersion!,
                                          Arg.Any<string>(),
                                          Arg.Any<CancellationToken>())
                .Returns(_ =>
                         {
                             events.Add("catalog-published");
                             return true;
                         });
        catalogs.TryRollbackCandidatePublicationAsync(LibraryId,
                                                       PipelineResult().SubjectTaxonomyVersion!,
                                                       Arg.Any<string>(),
                                                       Arg.Any<CancellationToken>())
                .Returns(_ =>
                         {
                             events.Add("catalog-reverted");
                             return true;
                         });
        sources.TryUpdateDirectoryPublicationAsync(Arg.Is<IDirectoryPublicationLease>(lease =>
                                                                                           lease != null &&
                                                                                           lease.LibraryId ==
                                                                                           LibraryId &&
                                                                                           (lease.ScanRunId == ScanRunId ||
                                                                                            lease.ScanRunId ==
                                                                                            "scan-next")),
                                                    Arg.Is<string?>(value => value == null),
                                                    Arg.Any<DateTime?>(),
                                                    Arg.Any<string?>(),
                                                    Arg.Any<CancellationToken>())
               .Returns(call =>
                        {
                            publicationMetadata.LastPublishedVersion = call.ArgAt<string?>(3);
                            return true;
                         });
        sources.TryRestoreDirectoryPublicationAsync(Arg.Any<IDirectoryPublicationLease>(),
                                                     Arg.Any<string>(),
                                                     Arg.Any<DateTime?>(),
                                                     Arg.Any<string?>(),
                                                     Arg.Any<CancellationToken>())
               .Returns(call =>
                        {
                            events.Add("metadata-restored");
                            publicationMetadata.LastPublishedVersion = call.ArgAt<string?>(3);
                            return true;
                        });
        libraries.TryBeginDirectoryVersionCleanupAsync(LibraryId,
                                                       Arg.Any<string>(),
                                                       Arg.Any<string>(),
                                                       Arg.Any<CancellationToken>())
                 .Returns(true);
        libraries.TryRecordDirectoryVersionFailureAsync(Arg.Do<LibraryVersionRecord>(record =>
                                                             {
                                                                 versions.Add(record);
                                                                 events.Add(record.PublicationState.ToString());
                                                             }),
                                                         Arg.Any<string>(),
                                                         Arg.Any<CancellationToken>())
                 .Returns(true);
        libraries.UpsertLibraryAsync(Arg.Do<LibraryRecord>(_ => events.Add("pointer")),
                                     Arg.Any<CancellationToken>())
                 .Returns(Task.CompletedTask);
        deletion.DeleteScanCandidateUnderLeaseAsync(Arg.Any<string?>(),
                                                    Arg.Any<string>(),
                                                    Arg.Any<string>(),
                                                    Arg.Any<IDirectoryPublicationLease>(),
                                                    Arg.Any<ILibraryIngestionModeLease>(),
                                                    Arg.Any<CancellationToken>())
                .Returns(_ =>
                         {
                             events.Add("candidate-cleaned");
                             return EmptyDeletionResult;
                         });
        var coordinator = new DirectoryIngestionCoordinator(factory,
                                                            pipeline,
                                                            deletion,
                                                            NullLogger<DirectoryIngestionCoordinator>.Instance,
                                                            modeLeaseManager);
        return new CoordinatorFixture(coordinator,
                                      pipeline,
                                      sources,
                                      libraries,
                                      catalogs,
                                      deletion,
                                      library,
                                      publicationLease,
                                      modeLease,
                                      publicationMetadata,
                                      versions,
                                      events);
    }

    private static DirectoryIngestionRequest Request() => new()
        {
            LibraryId = LibraryId,
            Version = Version,
            QueuedAt = QueuedAt,
            ScanRunId = ScanRunId,
            Definition = Definition(),
            Profile = null
        };

    private static DirectoryLibraryDefinition Definition() => new()
        {
            Id = LibraryId,
            RootPath = RootPath,
            Recursive = true,
            AllowedExtensions = DirectoryScanLimits.SupportedExtensions,
            ExclusionPatterns = [],
            BindingStatus = DirectoryLibraryBindingStatus.Bound,
            RegisteredAtUtc = QueuedAt.UtcDateTime,
            RegistrationRevision = RegistrationRevision,
            RegistrationIncarnationId = RegistrationIncarnationId
        };

    private static DirectoryLibraryDefinition LeasedDefinition(string scanRunId) => Definition() with
        {
            PublicationLeaseScanRunId = scanRunId,
            PublicationLeaseRegistrationRevision = RegistrationRevision,
            PublicationLeaseExpiresAtUtc = QueuedAt.UtcDateTime.AddMinutes(5)
        };

    private static DirectoryIngestionPipelineResult PipelineResult() => new(
        DocumentsProcessed: 4,
        PagesIndexed: 6,
        ChunksIndexed: 8,
        EmbeddingProviderId: "scripted",
        EmbeddingModelName: "scripted-embedding-v1",
        EmbeddingDimensions: 2,
        ClassifierBackend: "scripted",
        ClassifierModel: "scripted-classifier-v1",
        SubjectTaxonomyVersion: "taxonomy-000001");

    private static (DirectoryEntrySnapshot Snapshot, StableFileReadResult Read) FileSnapshot(
        string fileName,
        string content,
        DateTime modified)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var snapshot = new DirectoryEntrySnapshot(Path.Combine(RootPath, fileName),
                                                  FileAttributes.Normal,
                                                  bytes.Length,
                                                  modified);
        return (snapshot, new StableFileReadResult(bytes, snapshot, snapshot, string.Empty, null));
    }

    private static DocumentIntakeResult SuccessfulIntake(string fileName) => new(
        Succeeded: true,
        ReasonCode: DocumentIntakeReasonCodes.Extracted,
        Detail: "The scripted document was extracted.",
        Title: Path.GetFileNameWithoutExtension(fileName),
        Sections: [new DocumentIntakeSection(0, "Heading", "Body", PageStart: 1, PageEnd: 1)],
        ExtractionArtifact: "{}"u8.ToArray(),
        ExtractionMediaType: "application/json",
        Provenance: new DocumentExtractionProvenance
                        {
                            ExtractorName = "scripted",
                            ExtractorVersion = "1"
                        });

    private static void AssertOrdered(List<string> events, params string[] expected)
    {
        var prior = -1;
        foreach(string item in expected)
        {
            int current = events.IndexOf(item);
            Assert.True(current > prior, $"Expected '{item}' after index {prior}; events: {string.Join(",", events)}");
            prior = current;
        }
    }

    private sealed record CoordinatorFixture(DirectoryIngestionCoordinator Coordinator,
                                             IDirectoryIngestionPipeline Pipeline,
                                             ISourceDocumentRepository Sources,
                                             ILibraryRepository Libraries,
                                             ISubjectCatalogRepository Catalogs,
                                             ILibraryDeletionService Deletion,
                                             LibraryRecord Library,
                                             TestDirectoryPublicationLease PublicationLease,
                                             ILibraryIngestionModeLease ModeLease,
                                             PublicationMetadataState PublicationMetadata,
                                             List<LibraryVersionRecord> VersionWrites,
                                             List<string> Events);

    private sealed class PublicationMetadataState
    {
        public string? LastPublishedVersion { get; set; }
    }

    private sealed class FixedEngineTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => QueuedAt.ToUniversalTime();
    }

    private sealed class TestDirectoryPublicationLease : IDirectoryPublicationLease
    {
        internal TestDirectoryPublicationLease(string libraryId,
                                               string scanRunId,
                                               long registrationRevision,
                                               IReadOnlyList<bool>? renewalResults)
        {
            LibraryId = libraryId;
            ScanRunId = scanRunId;
            RegistrationRevision = registrationRevision;
            mRenewalResults = new Queue<bool>(renewalResults ?? []);
        }

        private readonly Queue<bool> mRenewalResults;

        public string LibraryId { get; }

        public string ScanRunId { get; }

        public string? RegistrationIncarnationId => DirectoryIngestionCoordinatorTests.RegistrationIncarnationId;

        public long RegistrationRevision { get; }

        public CancellationToken OwnershipLostToken => CancellationToken.None;

        public int RenewalCount { get; private set; }

        public ValueTask<bool> TryRenewAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RenewalCount++;
            bool result = mRenewalResults.Count == 0 || mRenewalResults.Dequeue();
            return ValueTask.FromResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly DateTimeOffset QueuedAt = new(2026, 8, 4, 23, 55, 0, TimeSpan.FromHours(-6));
    private static readonly LibraryDeletionResult EmptyDeletionResult = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    private const string LibraryId = "manual-library";
    private const string PriorVersion = "2026-08-03";
    private const string Version = "2026-08-04";
    private const string NextVersion = "2026-08-05";
    private const string ScanRunId = "scan-20260804";
    private const string RegistrationIncarnationId = "registration-incarnation";
    private const string RootPath = "C:\\manuals";
    private const long RegistrationRevision = 7;
}
