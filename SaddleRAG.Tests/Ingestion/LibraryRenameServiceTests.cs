// LibraryRenameServiceTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Services;

namespace SaddleRAG.Tests.Ingestion;

public sealed class LibraryRenameServiceTests
{
    [Fact]
    public async Task RenameLibraryCommitsMongoThenRebuildsTargetsAndCleansSourceIndexes()
    {
        RenameFixture fixture = MakeFixture();

        RenameLibraryResponse result = await fixture.Service.RenameLibraryAsync(Profile,
                                                                                  OldLibraryId,
                                                                                  NewLibraryId,
                                                                                  TestContext.Current.CancellationToken);

        Assert.Equal(RenameLibraryOutcome.Renamed, result.Outcome);
        Assert.Null(result.Warning);
        Assert.Equal(["database-flip", "rebuild-v1", "rebuild-v2", "cleanup-source"], fixture.Events);
        await fixture.Vector.Received(requiredNumberOfCalls: 1)
                     .IndexChunksAsync(Profile,
                                       NewLibraryId,
                                       FirstVersion,
                                       Arg.Is<IReadOnlyList<DocChunk>>(chunks => IsLibraryTarget(chunks,
                                                                                                 FirstVersion)),
                                       Arg.Any<CancellationToken>());
        await fixture.Vector.Received(requiredNumberOfCalls: 1)
                     .IndexChunksAsync(Profile,
                                       NewLibraryId,
                                       SecondVersion,
                                       Arg.Is<IReadOnlyList<DocChunk>>(chunks => IsLibraryTarget(chunks,
                                                                                                 SecondVersion)),
                                       Arg.Any<CancellationToken>());
        await fixture.Vector.Received(requiredNumberOfCalls: 1)
                     .RemoveLibraryIndexesAsync(Profile, OldLibraryId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenameLibraryDatabaseFailureDoesNotTouchVectorIndexes()
    {
        RenameFixture fixture = MakeFixture();
        fixture.Libraries.RenameAsync(OldLibraryId,
                                      NewLibraryId,
                                      Arg.Any<CancellationToken>())
               .Returns(call =>
                        {
                            fixture.Events.Add("database-flip");
                            return Task.FromException<RenameLibraryResponse>(
                                new InvalidOperationException(DatabaseFailure));
                        });
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.RenameLibraryAsync(Profile,
                                               OldLibraryId,
                                               NewLibraryId,
                                               TestContext.Current.CancellationToken));

        Assert.Equal(DatabaseFailure, exception.Message);
        Assert.Equal(["database-flip"], fixture.Events);
        await fixture.Vector.DidNotReceive()
                     .IndexChunksAsync(Arg.Any<string?>(),
                                       Arg.Any<string>(),
                                       Arg.Any<string>(),
                                       Arg.Any<IReadOnlyList<DocChunk>>(),
                                       Arg.Any<CancellationToken>());
        await fixture.Vector.DidNotReceive()
                     .RemoveLibraryIndexesAsync(Profile, OldLibraryId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenameLibraryRebuildFailureReturnsActionableWarningAndKeepsMongoRename()
    {
        RenameFixture fixture = MakeFixture();
        fixture.Vector.IndexChunksAsync(Profile,
                                        NewLibraryId,
                                        FirstVersion,
                                        Arg.Any<IReadOnlyList<DocChunk>>(),
                                        Arg.Any<CancellationToken>())
               .Returns(call =>
                        {
                            fixture.Events.Add("rebuild-failed");
                            throw new InvalidOperationException(VectorFailure);
                        });

        RenameLibraryResponse result = await fixture.Service.RenameLibraryAsync(Profile,
                                                                                  OldLibraryId,
                                                                                  NewLibraryId,
                                                                                  TestContext.Current.CancellationToken);

        Assert.Equal(RenameLibraryOutcome.Renamed, result.Outcome);
        string warning = Assert.IsType<string>(result.Warning);
        Assert.Contains(VectorFailure, warning, StringComparison.Ordinal);
        Assert.Contains("reembed_library", warning, StringComparison.Ordinal);
        Assert.Equal(["database-flip", "rebuild-failed"], fixture.Events);
        await fixture.Vector.DidNotReceive()
                     .RemoveLibraryIndexesAsync(Profile, OldLibraryId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenameVersionCommitsMongoThenRebuildsTargetAndCleansOnlyTheSourceVersion()
    {
        RenameFixture fixture = MakeFixture();

        RenameLibraryResponse result = await fixture.Service.RenameVersionAsync(Profile,
                                                                                  OldLibraryId,
                                                                                  FirstVersion,
                                                                                  NewVersion,
                                                                                  TestContext.Current.CancellationToken);

        Assert.Equal(RenameLibraryOutcome.Renamed, result.Outcome);
        Assert.Null(result.Warning);
        Assert.Equal(["database-version-flip", "rebuild-version", "cleanup-source-version"],
                     fixture.VersionEvents);
        await fixture.Vector.Received(requiredNumberOfCalls: 1)
                     .IndexChunksAsync(Profile,
                                       OldLibraryId,
                                       NewVersion,
                                       Arg.Is<IReadOnlyList<DocChunk>>(chunks => IsVersionTarget(chunks)),
                                       Arg.Any<CancellationToken>());
        await fixture.Vector.Received(requiredNumberOfCalls: 1)
                     .RemoveIndexAsync(Profile,
                                       OldLibraryId,
                                       FirstVersion,
                                       Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenameVersionDatabaseFailureDoesNotTouchVectorIndexes()
    {
        RenameFixture fixture = MakeFixture();
        fixture.Libraries.RenameVersionAsync(OldLibraryId,
                                             FirstVersion,
                                             NewVersion,
                                             Arg.Any<CancellationToken>())
               .Returns(call =>
                        {
                            fixture.VersionEvents.Add("database-version-flip");
                            return Task.FromException<RenameLibraryResponse>(
                                new InvalidOperationException(DatabaseFailure));
                        });
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.RenameVersionAsync(Profile,
                                               OldLibraryId,
                                               FirstVersion,
                                               NewVersion,
                                               TestContext.Current.CancellationToken));

        Assert.Equal(DatabaseFailure, exception.Message);
        Assert.Equal(["database-version-flip"], fixture.VersionEvents);
        await fixture.Vector.DidNotReceive()
                     .IndexChunksAsync(Arg.Any<string?>(),
                                       Arg.Any<string>(),
                                       Arg.Any<string>(),
                                       Arg.Any<IReadOnlyList<DocChunk>>(),
                                       Arg.Any<CancellationToken>());
        await fixture.Vector.DidNotReceive()
                     .RemoveIndexAsync(Profile,
                                      OldLibraryId,
                                      FirstVersion,
                                      Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenameVersionCleanupFailureReturnsActionableWarningWithoutUndoingRename()
    {
        RenameFixture fixture = MakeFixture();
        fixture.Vector.RemoveIndexAsync(Profile,
                                        OldLibraryId,
                                        FirstVersion,
                                        Arg.Any<CancellationToken>())
               .Returns(call =>
                        {
                            fixture.VersionEvents.Add("cleanup-failed");
                            throw new InvalidOperationException(VectorFailure);
                        });

        RenameLibraryResponse result = await fixture.Service.RenameVersionAsync(Profile,
                                                                                  OldLibraryId,
                                                                                  FirstVersion,
                                                                                  NewVersion,
                                                                                  TestContext.Current.CancellationToken);

        Assert.Equal(RenameLibraryOutcome.Renamed, result.Outcome);
        string warning = Assert.IsType<string>(result.Warning);
        Assert.Contains(VectorFailure, warning, StringComparison.Ordinal);
        Assert.Contains("reembed_library", warning, StringComparison.Ordinal);
        Assert.Equal(["database-version-flip", "rebuild-version", "cleanup-failed"],
                     fixture.VersionEvents);
    }

    private static RenameFixture MakeFixture()
    {
        var events = new List<string>();
        var versionEvents = new List<string>();
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var libraries = Substitute.For<ILibraryRepository>();
        var chunks = Substitute.For<IChunkRepository>();
        var vector = Substitute.For<IVectorSearchProvider>();
        factory.GetLibraryRepository(Profile).Returns(libraries);
        factory.GetChunkRepository(Profile).Returns(chunks);
        libraries.GetVersionsAsync(NewLibraryId, Arg.Any<CancellationToken>())
                 .Returns([VersionRecord(NewLibraryId, FirstVersion),
                           VersionRecord(NewLibraryId, SecondVersion)]);
        chunks.GetChunksAsync(NewLibraryId, FirstVersion, Arg.Any<CancellationToken>())
              .Returns([Chunk(NewLibraryId, FirstVersion)]);
        chunks.GetChunksAsync(NewLibraryId, SecondVersion, Arg.Any<CancellationToken>())
              .Returns([Chunk(NewLibraryId, SecondVersion)]);
        chunks.GetChunksAsync(OldLibraryId, NewVersion, Arg.Any<CancellationToken>())
              .Returns([Chunk(OldLibraryId, NewVersion)]);
        libraries.RenameAsync(OldLibraryId, NewLibraryId, Arg.Any<CancellationToken>())
                 .Returns(call =>
                          {
                              events.Add("database-flip");
                              return Renamed();
                          });
        vector.IndexChunksAsync(Profile,
                                NewLibraryId,
                                FirstVersion,
                                Arg.Any<IReadOnlyList<DocChunk>>(),
                                Arg.Any<CancellationToken>())
              .Returns(call =>
                       {
                           events.Add("rebuild-v1");
                           return Task.CompletedTask;
                       });
        vector.IndexChunksAsync(Profile,
                                NewLibraryId,
                                SecondVersion,
                                Arg.Any<IReadOnlyList<DocChunk>>(),
                                Arg.Any<CancellationToken>())
              .Returns(call =>
                       {
                           events.Add("rebuild-v2");
                           return Task.CompletedTask;
                       });
        vector.RemoveLibraryIndexesAsync(Profile, OldLibraryId, Arg.Any<CancellationToken>())
              .Returns(call =>
                       {
                           events.Add("cleanup-source");
                           return Task.CompletedTask;
                       });

        libraries.RenameVersionAsync(OldLibraryId,
                                     FirstVersion,
                                     NewVersion,
                                     Arg.Any<CancellationToken>())
                 .Returns(call =>
                          {
                              versionEvents.Add("database-version-flip");
                              return Renamed();
                          });
        vector.IndexChunksAsync(Profile,
                                OldLibraryId,
                                NewVersion,
                                Arg.Any<IReadOnlyList<DocChunk>>(),
                                Arg.Any<CancellationToken>())
              .Returns(call =>
                       {
                           versionEvents.Add("rebuild-version");
                           return Task.CompletedTask;
                       });
        vector.RemoveIndexAsync(Profile,
                                OldLibraryId,
                                FirstVersion,
                                Arg.Any<CancellationToken>())
              .Returns(call =>
                       {
                           versionEvents.Add("cleanup-source-version");
                           return Task.CompletedTask;
                       });
        ILibraryRenameService service = new LibraryRenameService(factory, vector);
        return new RenameFixture(service, libraries, vector, events, versionEvents);
    }

    private static bool IsLibraryTarget(IReadOnlyList<DocChunk>? chunks, string version)
    {
        bool result = chunks is not null
                      && chunks.Count == 1
                      && chunks[index: 0].Id == $"{NewLibraryId}/{version}/chunk-1"
                      && chunks[index: 0].LibraryId == NewLibraryId
                      && chunks[index: 0].Version == version
                      && chunks[index: 0].PageUrl == $"{SourceUri(NewLibraryId)}#section-1"
                      && chunks[index: 0].DocumentSource?.DocumentId == DocumentId
                      && chunks[index: 0].DocumentSource?.RevisionId == RevisionId(NewLibraryId, version)
                      && chunks[index: 0].DocumentSource?.SourceUri == SourceUri(NewLibraryId);
        return result;
    }

    private static bool IsVersionTarget(IReadOnlyList<DocChunk>? chunks)
    {
        bool result = chunks is not null
                      && chunks.Count == 1
                      && chunks[index: 0].Id == $"{OldLibraryId}/{NewVersion}/chunk-1"
                      && chunks[index: 0].LibraryId == OldLibraryId
                      && chunks[index: 0].Version == NewVersion
                      && chunks[index: 0].PageUrl == $"{SourceUri(OldLibraryId)}#section-1"
                      && chunks[index: 0].DocumentSource?.DocumentId == DocumentId
                      && chunks[index: 0].DocumentSource?.RevisionId == RevisionId(OldLibraryId, NewVersion)
                      && chunks[index: 0].DocumentSource?.SourceUri == SourceUri(OldLibraryId);
        return result;
    }

    private static DocChunk Chunk(string libraryId, string version) => new()
        {
            Id = $"{libraryId}/{version}/chunk-1",
            LibraryId = libraryId,
            Version = version,
            PageUrl = $"{SourceUri(libraryId)}#section-1",
            PageTitle = "Manual",
            Category = DocCategory.HowTo,
            Content = "rename marker",
            TokenCount = 2,
            Embedding = [1.0f, 0.0f],
            DocumentSource = new DocumentProvenance
                                 {
                                     DocumentId = DocumentId,
                                     RevisionId = RevisionId(libraryId, version),
                                     SourceUri = SourceUri(libraryId),
                                     RelativePath = "manual.pdf"
                                 }
        };

    private static LibraryVersionRecord VersionRecord(string libraryId, string version) => new()
        {
            Id = $"{libraryId}/{version}",
            LibraryId = libraryId,
            Version = version,
            ScrapedAt = RecordedAt,
            PageCount = 1,
            ChunkCount = 1,
            EmbeddingProviderId = "test",
            EmbeddingModelName = "test",
            EmbeddingDimensions = 2,
            PublicationState = VersionPublicationState.Published
        };

    private static RenameLibraryResponse Renamed() => new(
        RenameLibraryOutcome.Renamed,
        new RenameLibraryResult(Libraries: 1,
                                Versions: 1,
                                Chunks: 1,
                                Pages: 1,
                                Profiles: 0,
                                Indexes: 0,
                                Bm25Shards: 0,
                                ExcludedSymbols: 0,
                                ScrapeJobs: 0));

    private static string RevisionId(string libraryId, string version) =>
        SourceDocumentRepository.MakeRevisionId(libraryId, version, DocumentId);

    private static string SourceUri(string libraryId) =>
        $"saddlerag://library/{libraryId}/documents/{DocumentId}";

    private sealed record RenameFixture(ILibraryRenameService Service,
                                        ILibraryRepository Libraries,
                                        IVectorSearchProvider Vector,
                                        List<string> Events,
                                        List<string> VersionEvents);

    private static readonly DateTime RecordedAt = new(year: 2026,
                                                      month: 8,
                                                      day: 4,
                                                      hour: 18,
                                                      minute: 0,
                                                      second: 0,
                                                      DateTimeKind.Utc);
    private const string DatabaseFailure = "database flip failed";
    private const string VectorFailure = "vector maintenance failed";
    private const string Profile = "profile-a";
    private const string OldLibraryId = "manual-library";
    private const string NewLibraryId = "renamed-library";
    private const string FirstVersion = "v1";
    private const string SecondVersion = "v2";
    private const string NewVersion = "v1-renamed";
    private const string DocumentId = "document-1";
}
