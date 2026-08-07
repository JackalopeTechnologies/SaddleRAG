// Stage 7 RED contract for directory-document mutation-tool dry runs.

using System.Text.Json.Nodes;
using NSubstitute.Core;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;

namespace SaddleRAG.Tests.Mcp;

public sealed class DirectoryLifecycleMutationToolsTests
{
    [Fact]
    public async Task DeleteVersionDryRunReportsDocumentAndSubjectStoresWithoutEchoingTheRoot()
    {
        Fixture fixture = BuildFixture([Version]);
        fixture.Sources.GetRevisionsAsync(LibraryId, Version, Arg.Any<CancellationToken>())
               .Returns(Revisions(Version));
        fixture.Assignments.GetByDocumentRevisionIdsAsync(Arg.Any<IReadOnlyCollection<string>>(),
                                                           Arg.Any<CancellationToken>())
               .Returns(Assignments(Version));
        fixture.Sources.GetDirectoryDefinitionAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(Definition());

        string json = await MutationTools.DeleteVersion(fixture.Factory,
                                                         NoopRunner(),
                                                         fixture.Deletion,
                                                         LibraryId,
                                                         Version,
                                                         dryRun: true,
                                                         profile: null,
                                                         TestContext.Current.CancellationToken);

        JsonObject wouldDelete = JsonNode.Parse(json)!["WouldDelete"]!.AsObject();
        Assert.Equal(2, wouldDelete["DocumentRevisions"]!.GetValue<int>());
        Assert.Equal(2, wouldDelete["SourceDocuments"]!.GetValue<int>());
        Assert.Equal(2, wouldDelete["SubjectAssignments"]!.GetValue<int>());
        Assert.Equal(3, wouldDelete["ArtifactReferences"]!.GetValue<int>());
        Assert.DoesNotContain(RootPath, json, StringComparison.OrdinalIgnoreCase);
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteVersionAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeleteLibraryDryRunAggregatesUniqueDocumentsRevisionsSubjectsAndCatalogs()
    {
        Fixture fixture = BuildFixture([FirstVersion, SecondVersion]);
        fixture.Sources.GetRevisionsAsync(LibraryId, FirstVersion, Arg.Any<CancellationToken>())
               .Returns(Revisions(FirstVersion, sharedDocument: true));
        fixture.Sources.GetRevisionsAsync(LibraryId, SecondVersion, Arg.Any<CancellationToken>())
               .Returns(Revisions(SecondVersion, sharedDocument: true));
        fixture.Assignments.GetByDocumentRevisionIdsAsync(Arg.Any<IReadOnlyCollection<string>>(),
                                                           Arg.Any<CancellationToken>())
               .Returns(call => AssignmentsForRequestedRevisions(RequiredRevisionIds(call)));
        fixture.Catalogs.GetManyAsync(Arg.Any<IReadOnlyCollection<SubjectCatalogKey>>(),
                                      Arg.Any<CancellationToken>())
               .Returns([Catalog()]);
        fixture.Sources.GetDirectoryDefinitionAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(Definition());

        string json = await MutationTools.DeleteLibrary(fixture.Factory,
                                                         NoopRunner(),
                                                         fixture.Deletion,
                                                         LibraryId,
                                                         dryRun: true,
                                                         profile: null,
                                                         TestContext.Current.CancellationToken);

        JsonObject wouldDelete = JsonNode.Parse(json)!["WouldDelete"]!.AsObject();
        Assert.Equal(1, wouldDelete["DirectoryLibraries"]!.GetValue<int>());
        Assert.Equal(1, wouldDelete["SourceDocuments"]!.GetValue<int>());
        Assert.Equal(4, wouldDelete["DocumentRevisions"]!.GetValue<int>());
        Assert.Equal(4, wouldDelete["SubjectAssignments"]!.GetValue<int>());
        Assert.Equal(1, wouldDelete["SubjectCatalogs"]!.GetValue<int>());
        Assert.DoesNotContain(RootPath, json, StringComparison.OrdinalIgnoreCase);
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteLibraryAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RenameLibraryDryRunIncludesEveryNewStoreAndPreservesTheNoWriteBoundary()
    {
        Fixture fixture = BuildFixture([FirstVersion, SecondVersion]);
        fixture.Sources.GetRevisionsAsync(LibraryId, FirstVersion, Arg.Any<CancellationToken>())
               .Returns(Revisions(FirstVersion, sharedDocument: true));
        fixture.Sources.GetRevisionsAsync(LibraryId, SecondVersion, Arg.Any<CancellationToken>())
               .Returns(Revisions(SecondVersion, sharedDocument: true));
        fixture.Assignments.GetByDocumentRevisionIdsAsync(Arg.Any<IReadOnlyCollection<string>>(),
                                                           Arg.Any<CancellationToken>())
               .Returns(call => AssignmentsForRequestedRevisions(RequiredRevisionIds(call)));
        fixture.Catalogs.GetManyAsync(Arg.Any<IReadOnlyCollection<SubjectCatalogKey>>(),
                                      Arg.Any<CancellationToken>())
               .Returns([Catalog()]);
        fixture.Sources.GetDirectoryDefinitionAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(Definition());

        string json = await MutationTools.RenameLibrary(fixture.Factory,
                                                         NoopRunner(),
                                                         Substitute.For<ILibraryRenameService>(),
                                                         LibraryId,
                                                         newId: NewLibraryId,
                                                         version: null,
                                                         newVersion: null,
                                                         dryRun: true,
                                                         profile: null,
                                                         TestContext.Current.CancellationToken);

        JsonObject wouldRename = JsonNode.Parse(json)!["WouldRename"]!.AsObject();
        Assert.Equal(1, wouldRename["DirectoryLibraries"]!.GetValue<int>());
        Assert.Equal(1, wouldRename["SourceDocuments"]!.GetValue<int>());
        Assert.Equal(4, wouldRename["DocumentRevisions"]!.GetValue<int>());
        Assert.Equal(4, wouldRename["SubjectAssignments"]!.GetValue<int>());
        Assert.Equal(1, wouldRename["SubjectCatalogs"]!.GetValue<int>());
        Assert.DoesNotContain(RootPath, json, StringComparison.OrdinalIgnoreCase);
        await fixture.Libraries.DidNotReceive()
                     .RenameAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenameVersionDryRunReportsVersionScopedRevisionAndAssignmentCounts()
    {
        Fixture fixture = BuildFixture([Version]);
        fixture.Sources.GetRevisionsAsync(LibraryId, Version, Arg.Any<CancellationToken>())
               .Returns(Revisions(Version));
        fixture.Assignments.GetByDocumentRevisionIdsAsync(Arg.Any<IReadOnlyCollection<string>>(),
                                                           Arg.Any<CancellationToken>())
               .Returns(Assignments(Version));

        string json = await MutationTools.RenameLibrary(fixture.Factory,
                                                         NoopRunner(),
                                                         Substitute.For<ILibraryRenameService>(),
                                                         LibraryId,
                                                         newId: null,
                                                         version: Version,
                                                         newVersion: RenamedVersion,
                                                         dryRun: true,
                                                         profile: null,
                                                         TestContext.Current.CancellationToken);

        JsonObject wouldRename = JsonNode.Parse(json)!["WouldRename"]!.AsObject();
        Assert.Equal(2, wouldRename["DocumentRevisions"]!.GetValue<int>());
        Assert.Equal(2, wouldRename["SourceDocuments"]!.GetValue<int>());
        Assert.Equal(2, wouldRename["SubjectAssignments"]!.GetValue<int>());
        await fixture.Libraries.DidNotReceive()
                     .RenameVersionAsync(Arg.Any<string>(),
                                         Arg.Any<string>(),
                                         Arg.Any<string>(),
                                         Arg.Any<CancellationToken>());
    }

    private static Fixture BuildFixture(IReadOnlyList<string> versions)
    {
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var libraries = Substitute.For<ILibraryRepository>();
        var chunks = Substitute.For<IChunkRepository>();
        var pages = Substitute.For<IPageRepository>();
        var sources = Substitute.For<ISourceDocumentRepository>();
        var catalogs = Substitute.For<ISubjectCatalogRepository>();
        var assignments = Substitute.For<ISubjectAssignmentRepository>();
        var deletion = Substitute.For<ILibraryDeletionService>();
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraries);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunks);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(pages);
        factory.GetSourceDocumentRepository(Arg.Any<string?>()).Returns(sources);
        factory.GetSubjectCatalogRepository(Arg.Any<string?>()).Returns(catalogs);
        factory.GetSubjectAssignmentRepository(Arg.Any<string?>()).Returns(assignments);
        libraries.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
                 .Returns(new LibraryRecord
                              {
                                  Id = LibraryId,
                                  Name = "manuals",
                                  Hint = "stage 7",
                                  CurrentVersion = versions[^1],
                                  AllVersions = versions.ToList()
                              });
        libraries.GetLibraryAsync(NewLibraryId, Arg.Any<CancellationToken>())
                 .Returns((LibraryRecord?) null);
        foreach(string version in versions)
        {
            libraries.GetVersionAsync(LibraryId, version, Arg.Any<CancellationToken>())
                     .Returns(VersionRecord(version));
            chunks.GetChunkCountAsync(LibraryId, version, Arg.Any<CancellationToken>()).Returns(1);
            pages.GetPageCountAsync(LibraryId, version, Arg.Any<CancellationToken>()).Returns(1);
        }

        libraries.GetVersionAsync(LibraryId, RenamedVersion, Arg.Any<CancellationToken>())
                 .Returns((LibraryVersionRecord?) null);
        return new Fixture(factory, libraries, sources, catalogs, assignments, deletion);
    }

    private static IReadOnlyCollection<string> RequiredRevisionIds(CallInfo call)
    {
        ArgumentNullException.ThrowIfNull(call);
        IReadOnlyCollection<string>? revisionIds = call.ArgAt<IReadOnlyCollection<string>?>(position: 0);
        ArgumentNullException.ThrowIfNull(revisionIds);
        return revisionIds;
    }

    private static IReadOnlyList<DocumentRevisionRecord> Revisions(string version,
                                                                   bool sharedDocument = false)
    {
        string firstDocument = sharedDocument ? SharedDocumentId : $"document-{version}-one";
        string secondDocument = sharedDocument ? SharedDocumentId : $"document-{version}-two";
        return
        [
            Revision(version,
                     firstDocument,
                     $"revision-{version}-one",
                     originalHash: "shared-original",
                     extractionHash: "unique-extraction"),
            Revision(version,
                     secondDocument,
                     $"revision-{version}-two",
                     originalHash: "shared-original",
                     extractionHash: null)
        ];
    }

    private static DocumentRevisionRecord Revision(string version,
                                                   string documentId,
                                                   string revisionId,
                                                   string originalHash,
                                                   string? extractionHash) => new()
        {
            Id = revisionId,
            DocumentId = documentId,
            LibraryId = LibraryId,
            Version = version,
            ScanRunId = $"scan-{version}",
            State = DocumentRevisionState.Published,
            AcquiredAtUtc = RecordedAt,
            OriginalArtifactHash = originalHash,
            OriginalByteLength = 10,
            OriginalMediaType = "application/pdf",
            ExtractionArtifactHash = extractionHash,
            ExtractionByteLength = extractionHash == null ? null : 20,
            ExtractionMediaType = extractionHash == null ? null : "application/json"
        };

    private static IReadOnlyList<SubjectAssignmentRecord> Assignments(string version) =>
        AssignmentsForRequestedRevisions(Revisions(version).Select(item => item.Id).ToArray());

    private static IReadOnlyList<SubjectAssignmentRecord> AssignmentsForRequestedRevisions(
        IReadOnlyCollection<string> revisionIds) =>
        revisionIds.Select(revisionId => new SubjectAssignmentRecord
                                             {
                                                 Id = $"{LibraryId}/subjects/{revisionId}",
                                                 LibraryId = LibraryId,
                                                 Version = VersionFromRevision(revisionId),
                                                 ScanRunId = "stage7-scan",
                                                 DocumentId = SharedDocumentId,
                                                 DocumentRevisionId = revisionId,
                                                 TaxonomyVersion = TaxonomyVersion,
                                                 Primary = new SubjectSelection
                                                               {
                                                                   SubjectId = SubjectId,
                                                                   Confidence = 0.9f
                                                               },
                                                 NeedsReview = false,
                                                 Provenance = ClassifierProvenance()
                                             })
                   .ToArray();

    private static SubjectCatalogRecord Catalog() => new()
        {
            Id = $"{LibraryId}/{TaxonomyVersion}",
            LibraryId = LibraryId,
            Revision = 1,
            TaxonomyVersion = TaxonomyVersion,
            Concepts =
            [
                new SubjectConcept
                    {
                        Id = SubjectId,
                        Label = "Hydraulics",
                        Description = "Hydraulic service"
                    }
            ],
            Provenance = ClassifierProvenance(),
            CreatedAtUtc = RecordedAt
        };

    private static DirectoryLibraryDefinition Definition() => new()
        {
            Id = LibraryId,
            RootPath = RootPath,
            Recursive = true,
            AllowedExtensions = [".pdf"],
            BindingStatus = DirectoryLibraryBindingStatus.Bound,
            RegisteredAtUtc = RecordedAt
        };

    private static LibraryVersionRecord VersionRecord(string version) => new()
        {
            Id = $"{LibraryId}/{version}",
            LibraryId = LibraryId,
            Version = version,
            ScrapedAt = RecordedAt,
            PageCount = 1,
            ChunkCount = 1,
            EmbeddingProviderId = "stage7",
            EmbeddingModelName = "stage7",
            EmbeddingDimensions = 2,
            PublicationState = VersionPublicationState.Published
        };

    private static SubjectClassifierProvenance ClassifierProvenance() => new()
        {
            Backend = "stage7",
            ModelId = "stage7-model",
            PromptVersion = "subject-v1",
            GeneratedAtUtc = RecordedAt
        };

    private static string VersionFromRevision(string revisionId)
    {
        string result = revisionId.Contains(FirstVersion, StringComparison.Ordinal)
                            ? FirstVersion
                            : revisionId.Contains(SecondVersion, StringComparison.Ordinal)
                                ? SecondVersion
                                : Version;
        return result;
    }

    private static IBackgroundJobRunner NoopRunner()
    {
        var runner = Substitute.For<IBackgroundJobRunner>();
        runner.QueueAsync(Arg.Any<BackgroundJobRecord>(),
                          Arg.Any<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>(),
                          Arg.Any<CancellationToken>())
              .Returns("job");
        return runner;
    }

    private sealed record Fixture(RepositoryFactory Factory,
                                  ILibraryRepository Libraries,
                                  ISourceDocumentRepository Sources,
                                  ISubjectCatalogRepository Catalogs,
                                  ISubjectAssignmentRepository Assignments,
                                  ILibraryDeletionService Deletion);

    private static readonly DateTime RecordedAt = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
    private const string LibraryId = "stage7-mutation-library";
    private const string NewLibraryId = "stage7-mutation-renamed";
    private const string Version = "2026-08-04";
    private const string FirstVersion = "2026-08-03";
    private const string SecondVersion = "2026-08-04";
    private const string RenamedVersion = "manual-release";
    private const string SharedDocumentId = "shared-document";
    private const string TaxonomyVersion = "taxonomy-stage7";
    private const string SubjectId = "hydraulics";
    private const string RootPath = "C:\\private\\owned-manuals";
}
