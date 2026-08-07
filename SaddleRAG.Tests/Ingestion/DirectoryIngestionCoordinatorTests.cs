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
        AssertOrdered(events, "Building", "pipeline-complete", "revisions-published", "Published", "pointer");
        Assert.Equal(Version, fixture.Library.CurrentVersion);
        Assert.Contains(Version, fixture.Library.AllVersions);
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
    public async Task ConcurrentReregistrationDoesNotReplaceTheCapturedDefinition()
    {
        CoordinatorFixture fixture = MakeFixture();
        fixture.Pipeline.ExecuteAsync(Arg.Any<DirectoryIngestionRequest>(),
                                      Arg.Any<Action<DirectoryScanProgress>?>(),
                                      Arg.Any<CancellationToken>())
                .Returns(PipelineResult());
        fixture.Sources.TryUpdateDirectoryPublicationAsync(LibraryId,
                                                           RegistrationRevision,
                                                           expectedPublishedVersion: null,
                                                           QueuedAt.UtcDateTime,
                                                           Version,
                                                           Arg.Any<CancellationToken>())
               .Returns(false);

        DirectoryIngestionResult result = await fixture.Coordinator.RunAsync(Request(),
                                                                               onProgress: null,
                                                                               TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryIngestionStatuses.Completed, result.Status);
        await fixture.Sources.DidNotReceive()
                     .UpsertDirectoryDefinitionAsync(Arg.Any<DirectoryLibraryDefinition>(),
                                                     Arg.Any<CancellationToken>());
        await fixture.Sources.Received(requiredNumberOfCalls: 1)
                     .TryUpdateDirectoryPublicationAsync(LibraryId,
                                                         RegistrationRevision,
                                                         expectedPublishedVersion: null,
                                                         QueuedAt.UtcDateTime,
                                                         Version,
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

    private static CoordinatorFixture MakeFixture()
    {
        var events = new List<string>();
        var versions = new List<LibraryVersionRecord>();
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
        sources.PublishCandidateScanRunAsync(LibraryId,
                                             Arg.Any<string>(),
                                             Arg.Any<string>(),
                                             Arg.Any<CancellationToken>())
               .Returns(_ =>
                        {
                            events.Add("revisions-published");
                            return 4L;
                        });
        sources.TryUpdateDirectoryPublicationAsync(Arg.Is<string>(value => value == LibraryId),
                                                   Arg.Is<long>(value => value == RegistrationRevision),
                                                   Arg.Is<string?>(value => value == null),
                                                   Arg.Any<DateTime?>(),
                                                   Arg.Any<string?>(),
                                                   Arg.Any<CancellationToken>())
               .Returns(true);
        libraries.UpsertLibraryAsync(Arg.Do<LibraryRecord>(_ => events.Add("pointer")),
                                     Arg.Any<CancellationToken>())
                 .Returns(Task.CompletedTask);
        deletion.DeleteVersionAsync(Arg.Any<string?>(),
                                    Arg.Any<string>(),
                                    Arg.Any<string>(),
                                    Arg.Any<CancellationToken>())
                .Returns(EmptyDeletionResult);
        var coordinator = new DirectoryIngestionCoordinator(factory,
                                                            pipeline,
                                                            deletion,
                                                            NullLogger<DirectoryIngestionCoordinator>.Instance);
        return new CoordinatorFixture(coordinator, pipeline, sources, library, versions, events);
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
                                             LibraryRecord Library,
                                             List<LibraryVersionRecord> VersionWrites,
                                             List<string> Events);

    private sealed class FixedEngineTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => QueuedAt.ToUniversalTime();
    }

    private static readonly DateTimeOffset QueuedAt = new(2026, 8, 4, 23, 55, 0, TimeSpan.FromHours(-6));
    private static readonly LibraryDeletionResult EmptyDeletionResult = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    private const string LibraryId = "manual-library";
    private const string PriorVersion = "2026-08-03";
    private const string Version = "2026-08-04";
    private const string NextVersion = "2026-08-05";
    private const string ScanRunId = "scan-20260804";
    private const string RootPath = "C:\\manuals";
    private const long RegistrationRevision = 7;
}
