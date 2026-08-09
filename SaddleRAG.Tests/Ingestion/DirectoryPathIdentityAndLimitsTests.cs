// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Documents.Intake;
using SaddleRAG.Ingestion.Scanning;

namespace SaddleRAG.Tests.Ingestion;

public sealed class DirectoryPathIdentityAndLimitsTests
{
    [Fact]
    public void CaseSensitiveIdentityPreservesCanonicallyEquivalentUnicodeNames()
    {
        var identity = new DirectoryPathIdentity(isCaseSensitive: true);
        const string composed = "caf\u00e9.md";
        const string decomposed = "cafe\u0301.md";

        string composedIdentity = identity.NormalizeRelativePath(composed);
        string decomposedIdentity = identity.NormalizeRelativePath(decomposed);

        Assert.NotEqual(composedIdentity, decomposedIdentity);
        Assert.NotEqual(DirectoryPageProducer.MakeSourceDocumentId(LibraryId, composedIdentity),
                        DirectoryPageProducer.MakeSourceDocumentId(LibraryId, decomposedIdentity));
    }

    [Fact]
    public void ScanBudgetRejectsActualBytesBeyondTheAggregateBoundWithoutChangingItsReservation()
    {
        var budget = new DirectoryScanBudget(maxTotalBytes: 5, maxSectionCount: 1);

        Assert.True(budget.TryReserveBytes(4));
        Assert.False(budget.TryReserveBytes(2));
        Assert.True(budget.TryReserveBytes(1));
    }

    [Fact]
    public async Task CaseSensitiveIdentityRetainsDistinctFilesThatDifferOnlyByCase()
    {
        var identity = new DirectoryPathIdentity(isCaseSensitive: true);
        ScriptedDirectoryScanFileSystem fileSystem = FileSystem(identity.Comparer,
                                                                ("Guide.md", "upper"),
                                                                ("guide.md", "lower"));
        var sink = new RecordingSink();
        DirectoryScanReport report = await Engine(fileSystem, identity, SuccessfulIntake(sectionCount: 1))
            .ScanAsync(Request(), sink, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanStatus.Completed, report.Status);
        Assert.Equal(["Guide.md", "guide.md"],
                     sink.Documents.Select(document => document.Source.NormalizedRelativePath));
        Assert.NotEqual(DirectoryPageProducer.MakeSourceDocumentId(LibraryId, "Guide.md"),
                        DirectoryPageProducer.MakeSourceDocumentId(LibraryId, "guide.md"));
    }

    [Fact]
    public async Task CaseInsensitiveIdentityReportsCollisionAndAcceptsOnlyOneFile()
    {
        var identity = new DirectoryPathIdentity(isCaseSensitive: false);
        ScriptedDirectoryScanFileSystem fileSystem = FileSystem(identity.Comparer,
                                                                ("Guide.md", "same"),
                                                                ("guide.md", "same"));
        var sink = new RecordingSink();
        DirectoryScanReport report = await Engine(fileSystem, identity, SuccessfulIntake(sectionCount: 1))
            .ScanAsync(Request(), sink, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanStatus.CompletedWithErrors, report.Status);
        Assert.Single(sink.Documents);
        Assert.Equal("guide.md", sink.Documents[0].Source.NormalizedRelativePath);
        Assert.Contains(report.Entries,
                        entry => entry.ReasonCode == DirectoryScanReasonCodes.PathIdentityCollision);
        Assert.Equal(DirectoryPageProducer.MakeSourceDocumentId(LibraryId,
                                                                 identity.NormalizeRelativePath("Guide.md")),
                     DirectoryPageProducer.MakeSourceDocumentId(LibraryId,
                                                                 identity.NormalizeRelativePath("guide.md")));
    }

    [Fact]
    public async Task DocumentCountLimitFailsBeforeAnyFileIsReadOrAccepted()
    {
        var identity = new DirectoryPathIdentity(isCaseSensitive: true);
        ScriptedDirectoryScanFileSystem fileSystem = FileSystem(identity.Comparer);
        var yielded = 0;
        fileSystem.SetEnumeration(RootPath,
                                  new DirectoryEnumerationResult(Entries(), string.Empty, null));
        var intake = SuccessfulIntake(sectionCount: 1);
        var sink = new RecordingSink();
        DirectoryScanReport report = await Engine(fileSystem, identity, intake)
            .ScanAsync(Request(maxDocumentCount: 1),
                       sink,
                       cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanStatus.Failed, report.Status);
        Assert.Equal(DirectoryScanReasonCodes.DocumentCountLimitExceeded, report.ReasonCode);
        Assert.Equal(2, yielded);
        Assert.Empty(fileSystem.ReadPaths);
        Assert.Empty(sink.Documents);

        IEnumerable<DirectoryEntrySnapshot> Entries()
        {
            foreach(string name in new[] { "one.md", "two.md", "three.md" })
            {
                yielded++;
                yield return Snapshot(name, byteLength: 3);
            }
        }
    }

    [Fact]
    public async Task TotalByteLimitFailsBeforeAnyFileIsReadOrAccepted()
    {
        var identity = new DirectoryPathIdentity(isCaseSensitive: true);
        ScriptedDirectoryScanFileSystem fileSystem = FileSystem(identity.Comparer);
        var yielded = 0;
        fileSystem.SetEnumeration(RootPath,
                                  new DirectoryEnumerationResult(Entries(), string.Empty, null));
        var sink = new RecordingSink();
        DirectoryScanReport report = await Engine(fileSystem, identity, SuccessfulIntake(sectionCount: 1))
            .ScanAsync(Request(maxTotalBytes: 5),
                       sink,
                       cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanStatus.Failed, report.Status);
        Assert.Equal(DirectoryScanReasonCodes.TotalBytesLimitExceeded, report.ReasonCode);
        Assert.Equal(2, yielded);
        Assert.Empty(fileSystem.ReadPaths);
        Assert.Empty(sink.Documents);

        IEnumerable<DirectoryEntrySnapshot> Entries()
        {
            foreach(string name in new[] { "one.md", "two.md", "three.md" })
            {
                yielded++;
                yield return Snapshot(name, byteLength: 3);
            }
        }
    }

    [Fact]
    public async Task EntryCountLimitStopsLazyEnumerationAtTheFirstOverflowingEntry()
    {
        var identity = new DirectoryPathIdentity(isCaseSensitive: true);
        ScriptedDirectoryScanFileSystem fileSystem = FileSystem(identity.Comparer);
        var yielded = 0;
        fileSystem.SetEnumeration(RootPath,
                                  new DirectoryEnumerationResult(Entries(), string.Empty, null));

        DirectoryScanReport report = await Engine(fileSystem, identity, SuccessfulIntake(sectionCount: 1))
            .ScanAsync(Request(maxEntryCount: 2),
                       new RecordingSink(),
                       cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanStatus.Failed, report.Status);
        Assert.Equal(DirectoryScanReasonCodes.EntryCountLimitExceeded, report.ReasonCode);
        Assert.Equal(3, yielded);
        Assert.Empty(fileSystem.ReadPaths);

        IEnumerable<DirectoryEntrySnapshot> Entries()
        {
            foreach(string name in new[] { "one.bin", "two.bin", "three.bin", "four.bin" })
            {
                yielded++;
                yield return Snapshot(name, byteLength: 1);
            }
        }
    }

    [Fact]
    public async Task DirectoryCountLimitStopsBeforeEnumeratingTheOverflowingDirectory()
    {
        var identity = new DirectoryPathIdentity(isCaseSensitive: true);
        ScriptedDirectoryScanFileSystem fileSystem = FileSystem(identity.Comparer);
        fileSystem.SetEnumeration(RootPath,
                                  new DirectoryEnumerationResult(
                                      [Directory("one"), Directory("two")],
                                      string.Empty,
                                      null));

        DirectoryScanReport report = await Engine(fileSystem, identity, SuccessfulIntake(sectionCount: 1))
            .ScanAsync(Request(maxDirectoryCount: 1, recursive: true),
                       new RecordingSink(),
                       cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanStatus.Failed, report.Status);
        Assert.Equal(DirectoryScanReasonCodes.DirectoryCountLimitExceeded, report.ReasonCode);
        Assert.Equal([RootPath], fileSystem.EnumeratedPaths);
        Assert.Empty(fileSystem.ReadPaths);
    }

    [Fact]
    public async Task CancellationIsObservedBetweenLazyDirectoryEntries()
    {
        var identity = new DirectoryPathIdentity(isCaseSensitive: true);
        ScriptedDirectoryScanFileSystem fileSystem = FileSystem(identity.Comparer);
        using var cancellation = new CancellationTokenSource();
        var yielded = 0;
        fileSystem.SetEnumeration(RootPath,
                                  new DirectoryEnumerationResult(Entries(), string.Empty, null));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Engine(fileSystem, identity, SuccessfulIntake(sectionCount: 1))
                .ScanAsync(Request(), new RecordingSink(), cancellationToken: cancellation.Token));

        Assert.Equal(2, yielded);
        Assert.Empty(fileSystem.ReadPaths);

        IEnumerable<DirectoryEntrySnapshot> Entries()
        {
            yielded++;
            yield return Snapshot("one.bin", byteLength: 1);
            cancellation.Cancel();
            yielded++;
            yield return Snapshot("two.bin", byteLength: 1);
        }
    }

    [Fact]
    public async Task SectionLimitRejectsTheFirstDocumentBeyondTheAggregateBound()
    {
        var identity = new DirectoryPathIdentity(isCaseSensitive: true);
        ScriptedDirectoryScanFileSystem fileSystem = FileSystem(identity.Comparer,
                                                                ("one.md", "one"),
                                                                ("two.md", "two"));
        var sink = new RecordingSink();
        DirectoryScanReport report = await Engine(fileSystem, identity, SuccessfulIntake(sectionCount: 1))
            .ScanAsync(Request(maxSectionCount: 1),
                       sink,
                       cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanStatus.CompletedWithErrors, report.Status);
        Assert.Single(sink.Documents);
        Assert.Contains(report.Entries,
                        entry => entry.RelativePath == "two.md"
                                 && entry.ReasonCode == DirectoryScanReasonCodes.SectionCountLimitExceeded);
    }

    [Fact]
    public async Task ReusedDocumentReservesItsSectionsBeforeTheSinkAcceptsIt()
    {
        var identity = new DirectoryPathIdentity(isCaseSensitive: true);
        ScriptedDirectoryScanFileSystem fileSystem = FileSystem(identity.Comparer,
                                                                ("one.md", "one"),
                                                                ("two.md", "two"));
        var sink = new PreparingReuseSink();

        DirectoryScanReport report = await Engine(fileSystem, identity, SuccessfulIntake(sectionCount: 1))
            .ScanAsync(Request(maxSectionCount: 1),
                       sink,
                       cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanStatus.CompletedWithErrors, report.Status);
        Assert.Equal(2, sink.PreparedCount);
        Assert.Equal(1, sink.AcceptedCount);
        Assert.Contains(report.Entries,
                        entry => entry.RelativePath == "two.md"
                                 && entry.ReasonCode == DirectoryScanReasonCodes.SectionCountLimitExceeded);
    }

    private static DirectoryScanEngine Engine(ScriptedDirectoryScanFileSystem fileSystem,
                                               DirectoryPathIdentity identity,
                                               IDocumentIntake intake) =>
        new(fileSystem,
            intake,
            NullLogger<DirectoryScanEngine>.Instance,
            TimeProvider.System,
            identity,
            sharedLoggerCategory: true);

    private static ScriptedDirectoryScanFileSystem FileSystem(
        StringComparer comparer,
        params (string Name, string Content)[] files)
    {
        var result = new ScriptedDirectoryScanFileSystem(comparer);
        var root = new DirectoryEntrySnapshot(RootPath,
                                              FileAttributes.Directory,
                                              0,
                                              SourceTime);
        result.SetInspection(RootPath, new DirectoryPathResult(root, string.Empty, null));
        var entries = new List<DirectoryEntrySnapshot>(files.Length);
        foreach((string name, string content) in files)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            var snapshot = new DirectoryEntrySnapshot(Path.Combine(RootPath, name),
                                                      FileAttributes.Normal,
                                                      bytes.LongLength,
                                                      SourceTime);
            entries.Add(snapshot);
            result.SetRead(snapshot.FullPath,
                           new StableFileReadResult(bytes, snapshot, snapshot, string.Empty, null));
        }

        result.SetEnumeration(RootPath, new DirectoryEnumerationResult(entries, string.Empty, null));
        return result;
    }

    private static IDocumentIntake SuccessfulIntake(int sectionCount)
    {
        var result = Substitute.For<IDocumentIntake>();
        IReadOnlyList<DocumentIntakeSection> sections = Enumerable.Range(0, sectionCount)
                                                                  .Select(index => new DocumentIntakeSection(
                                                                              index,
                                                                              $"Section {index}",
                                                                              "content",
                                                                              null,
                                                                              null))
                                                                  .ToArray();
        result.ReadAsync(Arg.Any<DocumentIntakeRequest>(), Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult(new DocumentIntakeResult(true,
                                                                     DocumentIntakeReasonCodes.Extracted,
                                                                     "Extracted.",
                                                                     "Document",
                                                                     sections,
                                                                     "{}"u8.ToArray(),
                                                                     "application/json",
                                                                     null)));
        return result;
    }

    private static DirectoryEntrySnapshot Snapshot(string name, long byteLength) =>
        new(Path.Combine(RootPath, name), FileAttributes.Normal, byteLength, SourceTime);

    private static DirectoryEntrySnapshot Directory(string name) =>
        new(Path.Combine(RootPath, name), FileAttributes.Directory, 0, SourceTime);

    private static DirectoryScanRequest Request(int maxDocumentCount = 10,
                                                long maxTotalBytes = 1024,
                                                int maxSectionCount = 10,
                                                int maxDirectoryCount = 10,
                                                int maxEntryCount = 100,
                                                bool recursive = false) =>
        new()
            {
                LibraryId = LibraryId,
                ScanRunId = "scan-run",
                RootPath = RootPath,
                Recursive = recursive,
                MaxFileBytes = 1024,
                MaxDocumentCount = maxDocumentCount,
                MaxDirectoryCount = maxDirectoryCount,
                MaxEntryCount = maxEntryCount,
                MaxTotalBytes = maxTotalBytes,
                MaxSectionCount = maxSectionCount
            };

    private sealed class RecordingSink : IDirectoryScanSink
    {
        internal List<DirectoryAcquiredDocument> Documents { get; } = [];

        public Task AcceptAsync(DirectoryAcquiredDocument document, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Documents.Add(document);
            return Task.CompletedTask;
        }
    }

    private sealed class PreparingReuseSink : IDirectoryScanSink, IDirectoryScanReuseSink
    {
        internal int AcceptedCount { get; private set; }

        internal int PreparedCount { get; private set; }

        public Task AcceptAsync(DirectoryAcquiredDocument document, CancellationToken ct = default) =>
            throw new InvalidOperationException("Fresh extraction was not expected.");

        public PreparedDirectoryDocumentReuse? TryPrepareUnchanged(DirectoryStableDocument document)
        {
            PreparedCount++;
            return new PreparedDirectoryDocumentReuse(document, Prior());
        }

        public Task AcceptPreparedUnchangedAsync(PreparedDirectoryDocumentReuse prepared,
                                                 CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AcceptedCount++;
            return Task.CompletedTask;
        }

        private static PriorDirectoryDocument Prior()
        {
            var revision = new DocumentRevisionRecord
                               {
                                   Id = "revision",
                                   DocumentId = "document",
                                   LibraryId = LibraryId,
                                   Version = "version",
                                   ScanRunId = "scan-run",
                                   State = DocumentRevisionState.Published,
                                   AcquiredAtUtc = SourceTime,
                                   OriginalArtifactHash = "hash",
                                   OriginalByteLength = 1,
                                   OriginalMediaType = "text/markdown"
                               };
            var page = new PageRecord
                           {
                               Id = "page",
                               LibraryId = LibraryId,
                               Version = "version",
                               Url = "saddlerag://page",
                               Title = "Page",
                               Category = DocCategory.HowTo,
                               RawContent = "content",
                               FetchedAt = SourceTime,
                               ContentHash = "hash"
                           };
            return new PriorDirectoryDocument(revision, [page], []);
        }
    }

    private static readonly string RootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                                                                            "saddlerag-scripted-root"));
    private static readonly DateTime SourceTime = new(year: 2026,
                                                      month: 8,
                                                      day: 7,
                                                      hour: 12,
                                                      minute: 0,
                                                      second: 0,
                                                      DateTimeKind.Utc);
    private const string LibraryId = "case-library";
}
