// SourceDocumentRepositoryIntegrationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Tests.Database;

[Trait("Category", "Integration")]
public sealed class SourceDocumentRepositoryIntegrationTests : IAsyncLifetime
{
    private string mDatabaseName = string.Empty;
    private SaddleRagDbContext mContext = new SaddleRagDbContext(Options.Create(new SaddleRagDbSettings()));
    private SourceDocumentRepository mRepository = null!;

    public async ValueTask InitializeAsync()
    {
        mDatabaseName = $"saddlerag-documents-{Guid.NewGuid():N}";
        mContext = new SaddleRagDbContext(Options.Create(new SaddleRagDbSettings
                                                            {
                                                                ConnectionString = TestConnectionString,
                                                                DatabaseName = mDatabaseName
                                                            }));
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        mRepository = new SourceDocumentRepository(mContext);
    }

    public async ValueTask DisposeAsync()
    {
        await mContext.Database.Client.DropDatabaseAsync(mDatabaseName);
    }

    [Fact]
    public async Task DirectoryAndPathIdentityRoundTripWithoutContentIdentityCollapse()
    {
        var definition = Definition("manual-library", "C:\\Docs");
        await mRepository.UpsertDirectoryDefinitionAsync(definition, TestContext.Current.CancellationToken);

        var firstCandidate = Document("manual-library", "manuals/guide.pdf", "first-id");
        var samePathCandidate = Document("manual-library", "manuals/guide.pdf", "losing-id");
        var otherPathCandidate = Document("manual-library", "archive/guide.pdf", "second-id");
        var first = await mRepository.GetOrCreateDocumentAsync(firstCandidate, TestContext.Current.CancellationToken);
        var samePath = await mRepository.GetOrCreateDocumentAsync(samePathCandidate, TestContext.Current.CancellationToken);
        var otherPath = await mRepository.GetOrCreateDocumentAsync(otherPathCandidate, TestContext.Current.CancellationToken);

        DirectoryLibraryDefinition? storedDefinition = await mRepository.GetDirectoryDefinitionAsync(
                                                                  definition.Id,
                                                                  TestContext.Current.CancellationToken);
        Assert.Equivalent(definition, storedDefinition, strict: true);
        Assert.Equal(first.Id, samePath.Id);
        Assert.NotEqual(first.Id, otherPath.Id);
    }

    [Fact]
    public async Task UnboundDefinitionMayPersistWithoutRootWhileBoundDefinitionStillRequiresRoot()
    {
        DirectoryLibraryDefinition unbound = Definition("portable-library", string.Empty) with
                                                 {
                                                     BindingStatus = DirectoryLibraryBindingStatus.Unbound
                                                 };

        await mRepository.UpsertDirectoryDefinitionAsync(unbound, TestContext.Current.CancellationToken);

        DirectoryLibraryDefinition stored = Assert.IsType<DirectoryLibraryDefinition>(
            await mRepository.GetDirectoryDefinitionAsync(unbound.Id,
                                                           TestContext.Current.CancellationToken));
        Assert.Equal(DirectoryLibraryBindingStatus.Unbound, stored.BindingStatus);
        Assert.Empty(stored.RootPath);

        DirectoryLibraryDefinition bound = unbound with
                                               {
                                                   Id = "invalid-bound-library",
                                                   BindingStatus = DirectoryLibraryBindingStatus.Bound
                                               };
        await Assert.ThrowsAsync<ArgumentException>(() => mRepository.UpsertDirectoryDefinitionAsync(
                                                           bound,
                                                           TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentReregistrationRejectsStalePublicationMetadataUpdate()
    {
        DirectoryLibraryDefinition first = await mRepository.RegisterDirectoryDefinitionAsync(
                                               Definition("manual-library", "C:\\Docs"),
                                               TestContext.Current.CancellationToken);
        DirectoryLibraryDefinition second = await mRepository.RegisterDirectoryDefinitionAsync(
                                                Definition("manual-library", "D:\\NewDocs"),
                                                TestContext.Current.CancellationToken);
        var publishedAt = new DateTime(year: 2026,
                                       month: 8,
                                       day: 4,
                                       hour: 18,
                                       minute: 0,
                                       second: 0,
                                       DateTimeKind.Utc);

        bool staleUpdated = await mRepository.TryUpdateDirectoryPublicationAsync(
                                first.Id,
                                first.RegistrationRevision,
                                expectedPublishedVersion: null,
                                publishedAt,
                                "2026-08-04",
                                TestContext.Current.CancellationToken);
        bool currentUpdated = await mRepository.TryUpdateDirectoryPublicationAsync(
                                  second.Id,
                                  second.RegistrationRevision,
                                  expectedPublishedVersion: null,
                                  publishedAt,
                                  "2026-08-04",
                                  TestContext.Current.CancellationToken);
        DirectoryLibraryDefinition? stored = await mRepository.GetDirectoryDefinitionAsync(
                                                       second.Id,
                                                       TestContext.Current.CancellationToken);

        Assert.Equal(1, first.RegistrationRevision);
        Assert.Equal(2, second.RegistrationRevision);
        Assert.False(staleUpdated);
        Assert.True(currentUpdated);
        Assert.NotNull(stored);
        Assert.Equal("D:\\NewDocs", stored.RootPath);
        Assert.Equal(second.RegistrationRevision, stored.RegistrationRevision);
        Assert.Equal("2026-08-04", stored.LastPublishedVersion);
    }

    [Fact]
    public async Task ArtifactsRoundTripDeduplicateAndDeleteOnlyAfterFinalReference()
    {
        var original = "shared original bytes"u8.ToArray();
        var extraction = "structured extraction"u8.ToArray();
        var firstDocument = Document("manual-library", "one.pdf", "document-one");
        var secondDocument = Document("manual-library", "two.pdf", "document-two");
        await mRepository.GetOrCreateDocumentAsync(firstDocument, TestContext.Current.CancellationToken);
        await mRepository.GetOrCreateDocumentAsync(secondDocument, TestContext.Current.CancellationToken);

        var firstRevision = Revision(firstDocument, "2026-08-04", original, extraction);
        var secondRevision = Revision(secondDocument, "2026-08-04", original);
        await PersistAsync(firstRevision, original, extraction);
        await PersistAsync(secondRevision, original);

        Assert.Equal(original, await ReadArtifactAsync(Hash(original)));
        Assert.Equal(extraction, await ReadArtifactAsync(Hash(extraction)));
        Assert.Equal(2, await mContext.DocumentArtifactBlobs.CountDocumentsAsync(FilterDefinition<DocumentArtifactBlobRecord>.Empty,
                                                                                 cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await CountGridFsFilesAsync());

        Assert.True(await mRepository.DeleteRevisionAsync(firstRevision.Id, TestContext.Current.CancellationToken));
        Assert.Equal(original, await ReadArtifactAsync(Hash(original)));
        await Assert.ThrowsAsync<FileNotFoundException>(() => mRepository.OpenArtifactAsync(Hash(extraction),
                                                                                            TestContext.Current.CancellationToken));

        Assert.True(await mRepository.DeleteRevisionAsync(secondRevision.Id, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FileNotFoundException>(() => mRepository.OpenArtifactAsync(Hash(original),
                                                                                            TestContext.Current.CancellationToken));
        Assert.Equal(0, await CountGridFsFilesAsync());
    }

    [Fact]
    public async Task CandidateReplacementRemovesSupersededFinalReference()
    {
        var firstBytes = "first candidate"u8.ToArray();
        var replacementBytes = "replacement candidate"u8.ToArray();
        var document = Document("manual-library", "guide.pdf", "document-one");
        await mRepository.GetOrCreateDocumentAsync(document, TestContext.Current.CancellationToken);
        var first = Revision(document, "2026-08-04", firstBytes);
        var replacement = Revision(document, "2026-08-04", replacementBytes);

        await PersistAsync(first, firstBytes);
        await PersistAsync(replacement, replacementBytes);

        var stored = await mRepository.GetRevisionAsync(first.Id, TestContext.Current.CancellationToken);
        Assert.Equal(Hash(replacementBytes), stored?.OriginalArtifactHash);
        Assert.Equal(replacementBytes, await ReadArtifactAsync(Hash(replacementBytes)));
        await Assert.ThrowsAsync<FileNotFoundException>(() => mRepository.OpenArtifactAsync(Hash(firstBytes),
                                                                                            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RevisionWriteFailureCleansNewBlobAndPreservesSharedBlob()
    {
        var sharedBytes = "shared"u8.ToArray();
        var newBytes = "new and unreferenced"u8.ToArray();
        var sharedDocument = Document("manual-library", "shared.pdf", "shared-document");
        var conflictingDocument = Document("manual-library", "conflict.pdf", "conflicting-document");
        await mRepository.GetOrCreateDocumentAsync(sharedDocument, TestContext.Current.CancellationToken);
        await mRepository.GetOrCreateDocumentAsync(conflictingDocument, TestContext.Current.CancellationToken);
        await PersistAsync(Revision(sharedDocument, "2026-08-04", sharedBytes), sharedBytes);

        var seededConflict = Revision(conflictingDocument, "2026-08-04", sharedBytes) with { Id = "non-canonical-conflict" };
        await mContext.DocumentRevisions.InsertOneAsync(seededConflict,
                                                        cancellationToken: TestContext.Current.CancellationToken);
        var attempted = Revision(conflictingDocument, "2026-08-04", newBytes) with
                            {
                                ExtractionArtifactHash = Hash(sharedBytes),
                                ExtractionByteLength = sharedBytes.Length,
                                ExtractionMediaType = "application/json"
                            };

        await using var originalStream = new MemoryStream(newBytes, writable: false);
        await using var extractionStream = new MemoryStream(sharedBytes, writable: false);
        await Assert.ThrowsAnyAsync<MongoException>(() => mRepository.PersistRevisionAsync(
                                                        attempted,
                                                        originalStream,
                                                        extractionStream,
                                                        TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<FileNotFoundException>(() => mRepository.OpenArtifactAsync(Hash(newBytes),
                                                                                            TestContext.Current.CancellationToken));
        Assert.Equal(sharedBytes, await ReadArtifactAsync(Hash(sharedBytes)));
        Assert.Equal(1, await CountGridFsFilesAsync());
    }

    [Fact]
    public async Task NonSeekableHashMismatchRejectsRevisionAndDeletesUploadedBytes()
    {
        var declaredBytes = "declared"u8.ToArray();
        var differentBytes = "tampered"u8.ToArray();
        var document = Document("manual-library", "mismatch.pdf", "mismatch-document");
        await mRepository.GetOrCreateDocumentAsync(document, TestContext.Current.CancellationToken);
        var revision = Revision(document, "2026-08-04", declaredBytes);
        await using var source = new NonSeekableReadStream(new MemoryStream(differentBytes, writable: false));

        await Assert.ThrowsAsync<InvalidDataException>(() => mRepository.PersistRevisionAsync(
                                                            revision,
                                                            source,
                                                            extractionArtifact: null,
                                                            TestContext.Current.CancellationToken));

        Assert.Null(await mRepository.GetRevisionAsync(revision.Id, TestContext.Current.CancellationToken));
        Assert.Equal(0,
                     await mContext.DocumentArtifactBlobs.CountDocumentsAsync(
                         FilterDefinition<DocumentArtifactBlobRecord>.Empty,
                         cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, await CountGridFsFilesAsync());
    }

    [Fact]
    public async Task PublicationRacingCandidateReplacementWinsAndCleansLosingArtifact()
    {
        var publishedBytes = "published candidate"u8.ToArray();
        var replacementBytes = "losing replacement"u8.ToArray();
        var document = Document("manual-library", "race.pdf", "race-document");
        await mRepository.GetOrCreateDocumentAsync(document, TestContext.Current.CancellationToken);
        var published = Revision(document, "2026-08-04", publishedBytes);
        await PersistAsync(published, publishedBytes);
        var replacement = Revision(document, "2026-08-04", replacementBytes);
        await using var source = new BlockingReadStream(new MemoryStream(replacementBytes, writable: false));

        var replacementWrite = Task.Run(() => mRepository.PersistRevisionAsync(
                                            replacement,
                                            source,
                                            extractionArtifact: null,
                                            TestContext.Current.CancellationToken),
                                        TestContext.Current.CancellationToken);
        await source.WaitForReadAsync(TestContext.Current.CancellationToken);
        try
        {
            var update = Builders<DocumentRevisionRecord>.Update.Set(r => r.State,
                                                                      DocumentRevisionState.Published);
            await mContext.DocumentRevisions.UpdateOneAsync(r => r.Id == published.Id,
                                                            update,
                                                            cancellationToken:
                                                            TestContext.Current.CancellationToken);
        }
        finally
        {
            source.Release();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => replacementWrite);

        var stored = await mRepository.GetRevisionAsync(published.Id, TestContext.Current.CancellationToken);
        Assert.Equal(DocumentRevisionState.Published, stored?.State);
        Assert.Equal(Hash(publishedBytes), stored?.OriginalArtifactHash);
        Assert.Equal(publishedBytes, await ReadArtifactAsync(Hash(publishedBytes)));
        await Assert.ThrowsAsync<FileNotFoundException>(() => mRepository.OpenArtifactAsync(
                                                            Hash(replacementBytes),
                                                            TestContext.Current.CancellationToken));
        Assert.Equal(1, await CountGridFsFilesAsync());
    }

    private async Task PersistAsync(DocumentRevisionRecord revision, byte[] original, byte[]? extraction = null)
    {
        await using var originalStream = new MemoryStream(original, writable: false);
        await using var extractionStream = extraction == null ? null : new MemoryStream(extraction, writable: false);
        await mRepository.PersistRevisionAsync(revision,
                                               originalStream,
                                               extractionStream,
                                               TestContext.Current.CancellationToken);
    }

    private async Task<byte[]> ReadArtifactAsync(string hash)
    {
        await using var stream = await mRepository.OpenArtifactAsync(hash, TestContext.Current.CancellationToken);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
        return copy.ToArray();
    }

    private async Task<long> CountGridFsFilesAsync()
    {
        var files = mContext.Database.GetCollection<BsonDocument>("documentArtifacts.files");
        return await files.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
                                               cancellationToken: TestContext.Current.CancellationToken);
    }

    private static DirectoryLibraryDefinition Definition(string libraryId, string rootPath) => new()
        {
            Id = libraryId,
            RootPath = rootPath,
            RegisteredAtUtc = new DateTime(year: 2026,
                                           month: 8,
                                           day: 4,
                                           hour: 12,
                                           minute: 0,
                                           second: 0,
                                           DateTimeKind.Utc)
        };

    private static SourceDocumentRecord Document(string libraryId, string relativePath, string id) => new()
        {
            Id = id,
            LibraryId = libraryId,
            NormalizedRelativePath = relativePath,
            DisplayRelativePath = relativePath,
            DisplayName = Path.GetFileName(relativePath),
            SourceUri = $"saddlerag://library/{libraryId}/documents/{id}",
            MediaType = "application/pdf",
            FirstSeenVersion = "2026-08-04",
            CreatedAtUtc = DateTime.UtcNow
        };

    private static DocumentRevisionRecord Revision(SourceDocumentRecord document,
                                                   string version,
                                                   byte[] original,
                                                   byte[]? extraction = null) => new()
        {
            Id = SourceDocumentRepository.MakeRevisionId(document.LibraryId, version, document.Id),
            DocumentId = document.Id,
            LibraryId = document.LibraryId,
            Version = version,
            ScanRunId = "scan-run",
            State = DocumentRevisionState.Candidate,
            SourceModifiedAtUtc = DateTime.UtcNow,
            AcquiredAtUtc = DateTime.UtcNow,
            OriginalArtifactHash = Hash(original),
            OriginalByteLength = original.Length,
            OriginalMediaType = document.MediaType,
            ExtractionArtifactHash = extraction == null ? null : Hash(extraction),
            ExtractionByteLength = extraction?.Length,
            ExtractionMediaType = extraction == null ? null : "application/json"
        };

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private const string TestConnectionString = "mongodb://localhost:27017";
}
