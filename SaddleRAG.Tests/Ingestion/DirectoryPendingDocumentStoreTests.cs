// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Documents.Intake;
using SaddleRAG.Ingestion.Scanning;

namespace SaddleRAG.Tests.Ingestion;

public sealed class DirectoryPendingDocumentStoreTests
{
    [Fact]
    public async Task RoundTripRetainsDocumentDataWithoutSerializingTheCustomerRootAndCleansTheSpool()
    {
        string testRoot = TestRoot();
        Directory.CreateDirectory(testRoot);
        string? spoolRoot = null;
        try
        {
            await using(var store = new DirectoryPendingDocumentStore(maxDocumentCount: 2,
                                                                       maxBytes: 1024 * 1024,
                                                                       testRoot))
            {
                spoolRoot = store.RootPath;
                await store.AddAsync(Document(), TestContext.Current.CancellationToken);
                string payload = await File.ReadAllTextAsync(Path.Combine(spoolRoot, "00000000.json"),
                                                             TestContext.Current.CancellationToken);
                Assert.DoesNotContain(CustomerRoot, payload, StringComparison.OrdinalIgnoreCase);
                var documents = new List<PendingDirectoryDocument>();
                await foreach(PendingDirectoryDocument document in store.ReadAllAsync(
                                  TestContext.Current.CancellationToken))
                {
                    documents.Add(document);
                }

                PendingDirectoryDocument roundTrip = Assert.Single(documents);
                Assert.Equal("Guide.md", roundTrip.Source.DisplayRelativePath);
                Assert.Equal("Guide", roundTrip.Intake.Title);
                Assert.Equal("content", Assert.Single(roundTrip.Intake.Sections).Content);
            }

            Assert.False(Directory.Exists(spoolRoot));
        }
        finally
        {
            DeleteEmptyTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task CancellationAfterSpoolFileCreationStillCleansTheOwnedDirectory()
    {
        string testRoot = TestRoot();
        Directory.CreateDirectory(testRoot);
        string? spoolRoot = null;
        try
        {
            await using(var store = new DirectoryPendingDocumentStore(maxDocumentCount: 2,
                                                                       maxBytes: 1024 * 1024,
                                                                       testRoot))
            {
                spoolRoot = store.RootPath;
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AddAsync(Document(),
                                                                                               cancellation.Token));
                Assert.Empty(Directory.EnumerateFileSystemEntries(spoolRoot));
            }

            Assert.False(Directory.Exists(spoolRoot));
        }
        finally
        {
            DeleteEmptyTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task SpoolLimitFailureUsesAStableReasonAndStillCleansTheOwnedDirectory()
    {
        string testRoot = TestRoot();
        Directory.CreateDirectory(testRoot);
        string? spoolRoot = null;
        try
        {
            await using(var store = new DirectoryPendingDocumentStore(maxDocumentCount: 2,
                                                                       maxBytes: 1,
                                                                       testRoot))
            {
                spoolRoot = store.RootPath;
                DirectoryIngestionException error = await Assert.ThrowsAsync<DirectoryIngestionException>(
                    () => store.AddAsync(Document(), TestContext.Current.CancellationToken));
                Assert.Equal(DirectoryScanReasonCodes.PendingSpoolLimitExceeded, error.ReasonCode);
                Assert.Equal("Guide.md", error.RelativePath);
                Assert.Empty(Directory.EnumerateFileSystemEntries(spoolRoot));
            }

            Assert.False(Directory.Exists(spoolRoot));
        }
        finally
        {
            DeleteEmptyTestRoot(testRoot);
        }
    }

    [Fact]
    public void ReparsePointTempRootIsRejectedBeforeAnOwnedSpoolIsCreated()
    {
        string testRoot = TestRoot();
        string target = Path.Combine(testRoot, "target");
        string link = Path.Combine(testRoot, "link");
        Directory.CreateDirectory(target);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch(Exception error) when (error is IOException
                                         or UnauthorizedAccessException
                                         or PlatformNotSupportedException)
            {
                Assert.Skip($"Directory symbolic links are unavailable: {error.GetType().Name}.");
            }

            Assert.Throws<InvalidOperationException>(() =>
                new DirectoryPendingDocumentStore(maxDocumentCount: 1, maxBytes: 1024, link));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link, recursive: false);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: false);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: false);
        }
    }

    [Fact]
    public async Task CleanupFailureIsVisibleAndDoesNotRecursivelyDeleteUnexpectedContent()
    {
        string testRoot = TestRoot();
        Directory.CreateDirectory(testRoot);
        var store = new DirectoryPendingDocumentStore(maxDocumentCount: 1,
                                                       maxBytes: 1024 * 1024,
                                                       testRoot);
        string unexpectedDirectory = Path.Combine(store.RootPath, "unexpected");
        Directory.CreateDirectory(unexpectedDirectory);
        string unexpectedFile = Path.Combine(unexpectedDirectory, "keep.txt");
        await File.WriteAllTextAsync(unexpectedFile,
                                     "keep",
                                     TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<IOException>(async () => await store.DisposeAsync());
            Assert.True(File.Exists(unexpectedFile));
        }
        finally
        {
            File.Delete(unexpectedFile);
            Directory.Delete(unexpectedDirectory, recursive: false);
            await store.DisposeAsync();
            DeleteEmptyTestRoot(testRoot);
        }
    }

    [Fact]
    public void ChunkBudgetRejectsTheFirstBatchBeyondTheAggregateBoundWithoutChangingItsCount()
    {
        var budget = new DirectoryChunkBudget(maxChunkCount: 2);
        budget.Add(2, "Guide.md");

        DirectoryIngestionException error = Assert.Throws<DirectoryIngestionException>(() =>
            budget.Add(1, "Manual.pdf"));

        Assert.Equal(DirectoryScanReasonCodes.ChunkCountLimitExceeded, error.ReasonCode);
        Assert.Equal("Manual.pdf", error.RelativePath);
        Assert.Equal(2, budget.ChunkCount);
    }

    private static PendingDirectoryDocument Document()
    {
        var source = new SourceDocumentRecord
                         {
                             Id = "source-guide",
                             LibraryId = "manuals",
                             NormalizedRelativePath = "Guide.md",
                             DisplayRelativePath = "Guide.md",
                             DisplayName = "Guide.md",
                             SourceUri = "saddlerag://library/manuals/documents/source-guide",
                             MediaType = "text/markdown",
                             FirstSeenVersion = "version-1",
                             LastSeenVersion = "version-1",
                             CreatedAtUtc = Timestamp,
                             UpdatedAtUtc = Timestamp
                         };
        var revision = new DocumentRevisionRecord
                           {
                               Id = "revision-guide",
                               DocumentId = source.Id,
                               LibraryId = source.LibraryId,
                               Version = "version-1",
                               ScanRunId = "scan-run",
                               State = DocumentRevisionState.Candidate,
                               SourceModifiedAtUtc = Timestamp,
                               AcquiredAtUtc = Timestamp,
                               OriginalArtifactHash = "original-hash",
                               OriginalByteLength = 7,
                               OriginalMediaType = source.MediaType,
                               ExtractionArtifactHash = "extraction-hash",
                               ExtractionByteLength = 2,
                               ExtractionMediaType = "application/json"
                           };
        var intake = new DocumentIntakeResult(true,
                                              DocumentIntakeReasonCodes.Extracted,
                                              "Extracted.",
                                              "Guide",
                                              [new DocumentIntakeSection(0,
                                                                         "Guide",
                                                                         "content",
                                                                         null,
                                                                         null)],
                                              "{}"u8.ToArray(),
                                              "application/json",
                                              null);
        return new PendingDirectoryDocument(source, revision, intake, [], ReusedExtraction: false);
    }

    private static string TestRoot() => Path.Combine(Path.GetTempPath(),
                                                    $"saddlerag-pending-store-tests-{Guid.NewGuid():N}");

    private static void DeleteEmptyTestRoot(string testRoot)
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, recursive: false);
    }

    private static readonly DateTime Timestamp = new(year: 2026,
                                                      month: 8,
                                                      day: 7,
                                                      hour: 12,
                                                      minute: 0,
                                                      second: 0,
                                                      DateTimeKind.Utc);
    private static readonly string CustomerRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                                                                                "customer-library-root"));
}
