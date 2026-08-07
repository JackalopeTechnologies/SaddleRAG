// SourceDocumentCandidateCleanupTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Tests.Database;

[Trait("Category", "Integration")]
public sealed class SourceDocumentCandidateCleanupTests : IAsyncLifetime
{
    private string mDatabaseName = string.Empty;
    private SaddleRagDbContext mContext = new(Options.Create(new SaddleRagDbSettings()));
    private SourceDocumentRepository mRepository = null!;

    public async ValueTask InitializeAsync()
    {
        mDatabaseName = $"saddlerag-document-cleanup-{Guid.NewGuid():N}";
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
    public async Task CleanupDeletesOnlyMatchingCandidatesThenRemovesUnreferencedIdentitiesAndBlobs()
    {
        var uniqueBytes = "target-only"u8.ToArray();
        var sharedBytes = "shared-with-another-run"u8.ToArray();
        var publishedBytes = "published"u8.ToArray();
        var targetOnly = Document(WorkspaceLibraryId, "target-only.pdf", "target-only");
        var targetShared = Document(WorkspaceLibraryId, "target-shared.pdf", "target-shared");
        var otherRun = Document(WorkspaceLibraryId, "other-run.pdf", "other-run");
        var published = Document(WorkspaceLibraryId, "published.pdf", "published");
        var orphan = Document(WorkspaceLibraryId, "orphan.pdf", "orphan");
        var otherLibrary = Document("preview/other-library/scan-run", "other.pdf", "other-library");
        foreach(var document in new[] { targetOnly, targetShared, otherRun, published, orphan, otherLibrary })
            await mRepository.GetOrCreateDocumentAsync(document, TestContext.Current.CancellationToken);

        var targetOnlyRevision = Revision(targetOnly, TargetScanRunId, uniqueBytes, DocumentRevisionState.Candidate);
        var targetSharedRevision = Revision(targetShared, TargetScanRunId, sharedBytes, DocumentRevisionState.Candidate);
        var otherRunRevision = Revision(otherRun, "other-scan-run", sharedBytes, DocumentRevisionState.Candidate);
        var publishedRevision = Revision(published, TargetScanRunId, publishedBytes, DocumentRevisionState.Published);
        var otherLibraryRevision = Revision(otherLibrary,
                                            TargetScanRunId,
                                            "other library"u8.ToArray(),
                                            DocumentRevisionState.Candidate);
        await PersistAsync(targetOnlyRevision, uniqueBytes);
        await PersistAsync(targetSharedRevision, sharedBytes);
        await PersistAsync(otherRunRevision, sharedBytes);
        await PersistAsync(publishedRevision, publishedBytes);
        await PersistAsync(otherLibraryRevision, "other library"u8.ToArray());

        var deleted = await mRepository.DeleteCandidateScanRunAsync(WorkspaceLibraryId,
                                                                    TargetScanRunId,
                                                                    TestContext.Current.CancellationToken);

        Assert.Equal(2, deleted);
        Assert.Null(await mRepository.GetRevisionAsync(targetOnlyRevision.Id,
                                                       TestContext.Current.CancellationToken));
        Assert.Null(await mRepository.GetRevisionAsync(targetSharedRevision.Id,
                                                       TestContext.Current.CancellationToken));
        Assert.NotNull(await mRepository.GetRevisionAsync(otherRunRevision.Id,
                                                          TestContext.Current.CancellationToken));
        Assert.NotNull(await mRepository.GetRevisionAsync(publishedRevision.Id,
                                                          TestContext.Current.CancellationToken));
        Assert.NotNull(await mRepository.GetRevisionAsync(otherLibraryRevision.Id,
                                                          TestContext.Current.CancellationToken));
        Assert.Null(await mRepository.GetDocumentAsync(targetOnly.Id, TestContext.Current.CancellationToken));
        Assert.Null(await mRepository.GetDocumentAsync(targetShared.Id, TestContext.Current.CancellationToken));
        Assert.Null(await mRepository.GetDocumentAsync(orphan.Id, TestContext.Current.CancellationToken));
        Assert.NotNull(await mRepository.GetDocumentAsync(otherRun.Id, TestContext.Current.CancellationToken));
        Assert.NotNull(await mRepository.GetDocumentAsync(published.Id, TestContext.Current.CancellationToken));
        Assert.NotNull(await mRepository.GetDocumentAsync(otherLibrary.Id, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FileNotFoundException>(() => mRepository.OpenArtifactAsync(
                                                            Hash(uniqueBytes),
                                                            TestContext.Current.CancellationToken));
        Assert.Equal(sharedBytes, await ReadArtifactAsync(Hash(sharedBytes)));
        Assert.Equal(publishedBytes, await ReadArtifactAsync(Hash(publishedBytes)));
    }

    [Fact]
    public async Task CleanupIsIdempotentAndDoesNotUseVersionWideDeletion()
    {
        var bytes = "candidate"u8.ToArray();
        var document = Document(WorkspaceLibraryId, "candidate.pdf", "candidate");
        await mRepository.GetOrCreateDocumentAsync(document, TestContext.Current.CancellationToken);
        await PersistAsync(Revision(document, TargetScanRunId, bytes, DocumentRevisionState.Candidate), bytes);

        var first = await mRepository.DeleteCandidateScanRunAsync(WorkspaceLibraryId,
                                                                  TargetScanRunId,
                                                                  TestContext.Current.CancellationToken);
        var second = await mRepository.DeleteCandidateScanRunAsync(WorkspaceLibraryId,
                                                                   TargetScanRunId,
                                                                   TestContext.Current.CancellationToken);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(0,
                     await mContext.DocumentRevisions.CountDocumentsAsync(
                         FilterDefinition<DocumentRevisionRecord>.Empty,
                         cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task PersistAsync(DocumentRevisionRecord revision, byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        await mRepository.PersistRevisionAsync(revision,
                                               stream,
                                               extractionArtifact: null,
                                               TestContext.Current.CancellationToken);
    }

    private async Task<byte[]> ReadArtifactAsync(string hash)
    {
        await using var stream = await mRepository.OpenArtifactAsync(hash,
                                                                     TestContext.Current.CancellationToken);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
        return copy.ToArray();
    }

    private static SourceDocumentRecord Document(string libraryId, string relativePath, string id) => new()
        {
            Id = id,
            LibraryId = libraryId,
            NormalizedRelativePath = relativePath,
            DisplayRelativePath = relativePath,
            DisplayName = Path.GetFileName(relativePath),
            SourceUri = $"saddlerag://library/{libraryId}/documents/{id}",
            MediaType = "application/pdf",
            FirstSeenVersion = PreviewVersion,
            CreatedAtUtc = SourceTime
        };

    private static DocumentRevisionRecord Revision(SourceDocumentRecord document,
                                                   string scanRunId,
                                                   byte[] bytes,
                                                   DocumentRevisionState state) => new()
        {
            Id = SourceDocumentRepository.MakeRevisionId(document.LibraryId, PreviewVersion, document.Id),
            DocumentId = document.Id,
            LibraryId = document.LibraryId,
            Version = PreviewVersion,
            ScanRunId = scanRunId,
            State = state,
            SourceModifiedAtUtc = SourceTime,
            AcquiredAtUtc = SourceTime,
            OriginalArtifactHash = Hash(bytes),
            OriginalByteLength = bytes.Length,
            OriginalMediaType = document.MediaType,
            PublishedAtUtc = state == DocumentRevisionState.Published ? SourceTime : null
        };

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static readonly DateTime SourceTime = new(year: 2026,
                                                      month: 8,
                                                      day: 4,
                                                      hour: 12,
                                                      minute: 0,
                                                      second: 0,
                                                      DateTimeKind.Utc);
    private const string TestConnectionString = "mongodb://localhost:27017";
    private const string WorkspaceLibraryId = "preview/manual-library/target-scan-run";
    private const string TargetScanRunId = "target-scan-run";
    private const string PreviewVersion = "preview";
}
