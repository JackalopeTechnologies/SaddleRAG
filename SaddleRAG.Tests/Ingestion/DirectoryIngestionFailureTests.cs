// DirectoryIngestionFailureTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Scanning;

namespace SaddleRAG.Tests.Ingestion;

public sealed class DirectoryIngestionFailureTests
{
    [Fact]
    public async Task SupportedDocumentFailurePublishesNothingCleansCandidateAndLeavesPriorCurrent()
    {
        FailureFixture fixture = MakeFixture(existingAttempt: null);
        fixture.Pipeline.ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                      Arg.Any<Action<DirectoryScanProgress>?>(),
                                      Arg.Any<CancellationToken>())
                .Returns(Task.FromException<DirectoryIngestionPipelineResult>(
                             new DirectoryIngestionException(DirectoryScanReasonCodes.FileIoError,
                                                             "A supported document could not be read.")));

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryIngestionStatuses.Failed, result.Status);
        Assert.Equal(DirectoryScanReasonCodes.FileIoError, result.ReasonCode);
        Assert.Equal(PriorVersion, fixture.Library.CurrentVersion);
        Assert.DoesNotContain(Version, fixture.Library.AllVersions);
        Assert.DoesNotContain(fixture.VersionWrites,
                              record => record.PublicationState == VersionPublicationState.Published);
        Assert.Equal(VersionPublicationState.Failed, fixture.VersionWrites[^1].PublicationState);
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteVersionAsync(profile: null,
                                         LibraryId,
                                         Version,
                                         CancellationToken.None);
        await fixture.Libraries.DidNotReceive()
                     .UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancellationCleansCandidateBeforeRethrowAndLeavesPriorCurrent()
    {
        FailureFixture fixture = MakeFixture(existingAttempt: null);
        fixture.Pipeline.ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                      Arg.Any<Action<DirectoryScanProgress>?>(),
                                      Arg.Any<CancellationToken>())
                .Returns(call => Task.FromCanceled<DirectoryIngestionPipelineResult>(
                             call.Arg<CancellationToken>()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Coordinator.RunAsync(
                                                                    Request(),
                                                                    onProgress: null,
                                                                    cancellation.Token));

        Assert.Equal(PriorVersion, fixture.Library.CurrentVersion);
        Assert.DoesNotContain(fixture.VersionWrites,
                              record => record.PublicationState == VersionPublicationState.Published);
        Assert.Equal(VersionPublicationState.Failed, fixture.VersionWrites[^1].PublicationState);
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteVersionAsync(profile: null,
                                         LibraryId,
                                         Version,
                                         CancellationToken.None);
    }

    [Fact]
    public async Task FailedSameDateAttemptIsDeletedAndRetriedCleanly()
    {
        FailureFixture fixture = MakeFixture(VersionRecord(VersionPublicationState.Failed));
        fixture.Pipeline.ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                      Arg.Any<Action<DirectoryScanProgress>?>(),
                                      Arg.Any<CancellationToken>())
                .Returns(PipelineResult());

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryIngestionStatuses.Completed, result.Status);
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteVersionAsync(profile: null,
                                         LibraryId,
                                         Version,
                                         Arg.Any<CancellationToken>());
        await fixture.Pipeline.Received(requiredNumberOfCalls: 1)
                     .ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                   Arg.Any<Action<DirectoryScanProgress>?>(),
                                   Arg.Any<CancellationToken>());
        Assert.Equal(Version, fixture.Library.CurrentVersion);
    }

    [Fact]
    public async Task PublishedSameDateReturnsAlreadyScannedBeforeCandidateOrFileWrites()
    {
        FailureFixture fixture = MakeFixture(VersionRecord(VersionPublicationState.Published));

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanVersionProvider.AlreadyScannedTodayStatus, result.Status);
        Assert.Equal(Version, result.Version);
        await fixture.Pipeline.DidNotReceiveWithAnyArgs()
                     .ExecuteAsync(default!, default, TestContext.Current.CancellationToken);
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteVersionAsync(default, default!, default!, TestContext.Current.CancellationToken);
        await fixture.Sources.DidNotReceiveWithAnyArgs()
                     .PublishCandidateScanRunAsync(default!,
                                                   default!,
                                                   default!,
                                                   TestContext.Current.CancellationToken);
        Assert.Empty(fixture.VersionWrites);
    }

    [Fact]
    public async Task BuildingSameDateReturnsInProgressWithoutStartingAnotherScan()
    {
        FailureFixture fixture = MakeFixture(VersionRecord(VersionPublicationState.Building));

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanVersionProvider.ScanInProgressStatus, result.Status);
        await fixture.Pipeline.DidNotReceiveWithAnyArgs()
                     .ExecuteAsync(default!, default, TestContext.Current.CancellationToken);
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteVersionAsync(default, default!, default!, TestContext.Current.CancellationToken);
        Assert.Equal(PriorVersion, fixture.Library.CurrentVersion);
    }

    [Fact]
    public async Task FailureDoesNotCleanupWhenAnotherScanOwnsTheVersion()
    {
        FailureFixture fixture = MakeFixture(existingAttempt: null);
        fixture.Pipeline.ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                      Arg.Any<Action<DirectoryScanProgress>?>(),
                                      Arg.Any<CancellationToken>())
                .Returns(Task.FromException<DirectoryIngestionPipelineResult>(
                             new DirectoryIngestionException(DirectoryScanReasonCodes.FileIoError,
                                                             "A supported document could not be read.")));
        fixture.Libraries.TryBeginDirectoryVersionCleanupAsync(LibraryId,
                                                               Version,
                                                               ScanRunId,
                                                               Arg.Any<CancellationToken>())
               .Returns(false);

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryIngestionStatuses.Failed, result.Status);
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteVersionAsync(default, default!, default!, TestContext.Current.CancellationToken);
        await fixture.Libraries.DidNotReceiveWithAnyArgs()
                     .TryRecordDirectoryVersionFailureAsync(default!,
                                                            default!,
                                                            TestContext.Current.CancellationToken);
    }

    private static FailureFixture MakeFixture(LibraryVersionRecord? existingAttempt)
    {
        var writes = new List<LibraryVersionRecord>();
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var libraries = Substitute.For<ILibraryRepository>();
        var sources = Substitute.For<ISourceDocumentRepository>();
        var pipeline = Substitute.For<IDirectoryIngestionPipeline>();
        var deletion = Substitute.For<ILibraryDeletionService>();
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
        libraries.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(library);
        bool requiresCleanup = existingAttempt?.PublicationState == VersionPublicationState.Failed;
        libraries.TryClaimDirectoryVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                                Arg.Any<CancellationToken>())
                 .Returns(call =>
                          {
                              DirectoryVersionClaimResult result;
                              switch(existingAttempt?.PublicationState)
                              {
                                  case VersionPublicationState.Published:
                                      result = new DirectoryVersionClaimResult(
                                          DirectoryVersionClaimStatus.AlreadyPublished);
                                      break;
                                  case VersionPublicationState.Building:
                                      result = new DirectoryVersionClaimResult(
                                          DirectoryVersionClaimStatus.InProgress);
                                      break;
                                  default:
                                      LibraryVersionRecord? write = call.Arg<LibraryVersionRecord>();
                                      if (write == null)
                                          throw new InvalidDataException("A version claim write was not supplied.");
                                      writes.Add(write);
                                      result = new DirectoryVersionClaimResult(
                                          DirectoryVersionClaimStatus.Acquired,
                                          requiresCleanup);
                                      requiresCleanup = false;
                                      break;
                              }

                              return result;
                          });
        libraries.TryPublishDirectoryVersionAsync(Arg.Do<LibraryVersionRecord>(writes.Add),
                                                  Arg.Any<string>(),
                                                  Arg.Any<CancellationToken>())
                 .Returns(true);
        libraries.TryBeginDirectoryVersionCleanupAsync(LibraryId,
                                                       Version,
                                                       ScanRunId,
                                                       Arg.Any<CancellationToken>())
                 .Returns(true);
        libraries.TryRecordDirectoryVersionFailureAsync(Arg.Do<LibraryVersionRecord>(writes.Add),
                                                        ScanRunId,
                                                        Arg.Any<CancellationToken>())
                 .Returns(true);
        sources.PublishCandidateScanRunAsync(Arg.Any<string>(),
                                             Arg.Any<string>(),
                                             Arg.Any<string>(),
                                             Arg.Any<CancellationToken>())
               .Returns(4L);
        sources.TryUpdateDirectoryPublicationAsync(Arg.Any<string>(),
                                                   Arg.Any<long>(),
                                                   Arg.Any<string?>(),
                                                   Arg.Any<DateTime?>(),
                                                   Arg.Any<string?>(),
                                                   Arg.Any<CancellationToken>())
               .Returns(true);
        deletion.DeleteVersionAsync(Arg.Any<string?>(),
                                    Arg.Any<string>(),
                                    Arg.Any<string>(),
                                    Arg.Any<CancellationToken>())
                .Returns(EmptyDeletionResult);
        var coordinator = new DirectoryIngestionCoordinator(factory,
                                                            pipeline,
                                                            deletion,
                                                            NullLogger<DirectoryIngestionCoordinator>.Instance);
        return new FailureFixture(coordinator, pipeline, deletion, libraries, sources, library, writes);
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
            RegistrationRevision = RegistrationRevision
        };

    private static LibraryVersionRecord VersionRecord(VersionPublicationState state) => new()
        {
            Id = $"{LibraryId}/{Version}",
            LibraryId = LibraryId,
            Version = Version,
            ScrapedAt = QueuedAt.UtcDateTime,
            PageCount = 0,
            ChunkCount = 0,
            EmbeddingProviderId = "scripted",
            EmbeddingModelName = "scripted-embedding-v1",
            EmbeddingDimensions = 2,
            PublicationState = state
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

    private sealed record FailureFixture(DirectoryIngestionCoordinator Coordinator,
                                         IDirectoryIngestionPipeline Pipeline,
                                         ILibraryDeletionService Deletion,
                                         ILibraryRepository Libraries,
                                         ISourceDocumentRepository Sources,
                                         LibraryRecord Library,
                                         List<LibraryVersionRecord> VersionWrites);

    private static readonly DateTimeOffset QueuedAt = new(2026, 8, 4, 12, 0, 0, TimeSpan.FromHours(-6));
    private static readonly LibraryDeletionResult EmptyDeletionResult = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    private const string LibraryId = "manual-library";
    private const string RootPath = "C:\\manuals";
    private const string PriorVersion = "2026-08-03";
    private const string Version = "2026-08-04";
    private const string ScanRunId = "scan-1";
    private const long RegistrationRevision = 3;
}
