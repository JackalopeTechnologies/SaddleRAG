// DirectoryScannerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Documents.Intake;
using SaddleRAG.Ingestion.Documents.Docling;
using SaddleRAG.Ingestion.Scanning;

namespace SaddleRAG.Tests.Ingestion;

public sealed class DirectoryScannerTests
{
    [Fact]
    public async Task NonRecursivePreviewReadsOnlyRootFilesAndAlwaysCleansItsEphemeralWorkspace()
    {
        var fileSystem = RootedFileSystem();
        var rootFile = FileAt(RootPath, "guide.md", "# Guide");
        var nestedDirectory = DirectoryAt(RootPath, "nested");
        var nestedFile = FileAt(nestedDirectory.FullPath, "hidden.txt", "hidden");
        fileSystem.SetEnumeration(RootPath, Enumeration(rootFile.Snapshot, nestedDirectory));
        fileSystem.SetEnumeration(nestedDirectory.FullPath, Enumeration(nestedFile.Snapshot));
        fileSystem.SetRead(rootFile.Snapshot.FullPath, rootFile.Read);
        fileSystem.SetRead(nestedFile.Snapshot.FullPath, nestedFile.Read);
        var intake = SuccessfulIntake();
        var repository = CandidateRepository();
        var scanner = MakeScanner(fileSystem, intake, repository);

        var report = await scanner.ScanAsync(Request(recursive: false), TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanStatus.Completed, report.Status);
        Assert.Equal(["guide.md"], report.Entries.Select(entry => entry.RelativePath));
        Assert.DoesNotContain(nestedDirectory.FullPath, fileSystem.EnumeratedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(nestedFile.Snapshot.FullPath, fileSystem.ReadPaths, StringComparer.OrdinalIgnoreCase);
        await AssertEphemeralCandidateLifecycleAsync(repository);
    }

    [Fact]
    public async Task RecursivePreviewPreservesUnicodeIdentityAndOrdinallySortsSupportedPaths()
    {
        var fileSystem = RootedFileSystem();
        var nestedDirectory = DirectoryAt(RootPath, "e\u0301");
        var zeta = FileAt(RootPath, "Zeta.TXT", "zeta");
        var alpha = FileAt(RootPath, "alpha.HTML", "<main>alpha</main>");
        var hidden = FileAt(RootPath, ".Hidden.txt", "hidden");
        var nested = FileAt(nestedDirectory.FullPath, "Guide.Markdown", "# nested");
        fileSystem.SetEnumeration(RootPath,
                                  Enumeration(zeta.Snapshot,
                                              nestedDirectory,
                                              alpha.Snapshot,
                                              hidden.Snapshot));
        fileSystem.SetEnumeration(nestedDirectory.FullPath, Enumeration(nested.Snapshot));
        fileSystem.SetRead(zeta.Snapshot.FullPath, zeta.Read);
        fileSystem.SetRead(alpha.Snapshot.FullPath, alpha.Read);
        fileSystem.SetRead(hidden.Snapshot.FullPath, hidden.Read);
        fileSystem.SetRead(nested.Snapshot.FullPath, nested.Read);
        var intake = SuccessfulIntake();
        var repository = CandidateRepository();
        var scanner = MakeScanner(fileSystem, intake, repository);
        DirectoryPathIdentity identity = DirectoryPathIdentity.Platform;

        var report = await scanner.ScanAsync(Request(recursive: true), TestContext.Current.CancellationToken);

        IReadOnlyList<string> expected = new[]
                                          {
                                              ".Hidden.txt",
                                              "alpha.HTML",
                                              "Zeta.TXT",
                                              "e\u0301/Guide.Markdown"
                                          }
                                         .Select(identity.NormalizeRelativePath)
                                         .OrderBy(path => path, StringComparer.Ordinal)
                                         .ToList();
        Assert.Equal(expected,
                     report.Entries.Select(entry => entry.RelativePath));
        Assert.DoesNotContain(identity.NormalizeRelativePath("é/Guide.Markdown"),
                              report.Entries.Select(entry => entry.RelativePath));
        Assert.Equal(4, report.ExtractedCount);
        Assert.Equal(4, intake.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IDocumentIntake.ReadAsync)));
        await repository.Received()
                        .GetOrCreateDocumentAsync(Arg.Is<SourceDocumentRecord>(document => document != null
                                                                                  && document.NormalizedRelativePath
                                                                                      == identity.NormalizeRelativePath(
                                                                                          "Zeta.TXT")
                                                                                 && document.DisplayRelativePath
                                                                                     == "Zeta.TXT"),
                                                  Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PhysicalGeneratedTreeProducesIdenticalSanitizedReportsOnTwoExplicitPreviews()
    {
        using var fixture = DirectoryScanFixtureTree.Create();
        var mapped = new DoclingMappedDocument("manual",
                                               "# Converted\nBody.",
                                               "Converted\nBody.",
                                               "{\"status\":\"success\"}",
                                               "{\"document\":{}}",
                                               []);
        var docling = Substitute.For<IDoclingClient>();
        docling.ConvertAsync(Arg.Any<DoclingFile>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromResult(DoclingConversionResult.Success(mapped)));
        var scanner = MakeScanner(new PhysicalDirectoryScanFileSystem(),
                                  new DocumentIntakeService(docling),
                                  CandidateRepository());
        var request = new DirectoryScanRequest
                          {
                              LibraryId = LibraryId,
                              ScanRunId = ScanRunId,
                              RootPath = fixture.RootPath,
                              Recursive = true,
                              MaxFileBytes = DirectoryScanLimits.DefaultMaxFileBytes
                          };

        var first = await scanner.ScanAsync(request, TestContext.Current.CancellationToken);
        var second = await scanner.ScanAsync(request, TestContext.Current.CancellationToken);

        var firstJson = JsonSerializer.Serialize(first);
        var secondJson = JsonSerializer.Serialize(second);
        DirectoryPathIdentity identity = DirectoryPathIdentity.Platform;
        string notesPath = identity.NormalizeRelativePath("Notes.txt");
        string duplicatePath = identity.NormalizeRelativePath("Nested/Duplicate.txt");
        Assert.Equal(firstJson, secondJson);
        Assert.DoesNotContain(fixture.RootPath, firstJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[]
                         {
                             "Guide.md",
                             "Manual.docx",
                             "Manual.pdf",
                             "Nested/Duplicate.txt",
                             "Notes.txt",
                             "Page.html"
                         }.Select(identity.NormalizeRelativePath),
                     first.Entries.Where(entry => entry.Status == DirectoryScanEntryStatus.Extracted)
                          .Select(entry => entry.RelativePath));
        Assert.Equal(6, first.ExtractedCount);
        Assert.Equal(1, first.Entries.Count(entry => entry.RelativePath == notesPath));
        Assert.Equal(1, first.Entries.Count(entry => entry.RelativePath == duplicatePath));
        Assert.Equal(DirectoryScanReasonCodes.FileUnsupportedType,
                     Assert.Single(first.Entries,
                                   entry => entry.RelativePath == "unsupported.bin").ReasonCode);
        await docling.Received(requiredNumberOfCalls: 4)
                     .ConvertAsync(Arg.Any<DoclingFile>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(DirectoryScanReasonCodes.FileAccessDenied)]
    [InlineData(DirectoryScanReasonCodes.FileLocked)]
    [InlineData(DirectoryScanReasonCodes.FileIoError)]
    [InlineData(DirectoryScanReasonCodes.FileDisappeared)]
    public async Task StableReadFailuresRemainDistinctAndRawOsDetailsStayOutOfTheReport(string reasonCode)
    {
        var fileSystem = RootedFileSystem();
        var file = FileAt(RootPath, "manual.pdf", "pdf bytes");
        var secret = $"raw failure at {file.Snapshot.FullPath}";
        fileSystem.SetEnumeration(RootPath, Enumeration(file.Snapshot));
        fileSystem.SetRead(file.Snapshot.FullPath,
                           new StableFileReadResult(ReadOnlyMemory<byte>.Empty,
                                                    file.Snapshot,
                                                    null,
                                                    reasonCode,
                                                    new IOException(secret)));
        var scanner = MakeScanner(fileSystem, SuccessfulIntake(), CandidateRepository());

        var report = await scanner.ScanAsync(Request(recursive: false), TestContext.Current.CancellationToken);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(reasonCode, entry.ReasonCode);
        Assert.Equal(DirectoryScanStatus.CompletedWithErrors, report.Status);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(report), StringComparison.Ordinal);
        Assert.DoesNotContain(RootPath, JsonSerializer.Serialize(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BeforeAfterMetadataMismatchIsReportedAsChangedAndIsNeverIntaken()
    {
        var fileSystem = RootedFileSystem();
        var file = FileAt(RootPath, "moving.docx", "first");
        var changed = file.Snapshot with
                          {
                              ByteLength = file.Snapshot.ByteLength + 1,
                              LastWriteTimeUtc = file.Snapshot.LastWriteTimeUtc.AddSeconds(1)
                          };
        fileSystem.SetEnumeration(RootPath, Enumeration(file.Snapshot));
        fileSystem.SetRead(file.Snapshot.FullPath,
                           new StableFileReadResult(file.Read.Content,
                                                    file.Snapshot,
                                                    changed,
                                                    string.Empty,
                                                    null));
        var intake = SuccessfulIntake();
        var scanner = MakeScanner(fileSystem, intake, CandidateRepository());

        var report = await scanner.ScanAsync(Request(recursive: false), TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanReasonCodes.FileChangedDuringScan, Assert.Single(report.Entries).ReasonCode);
        Assert.Empty(intake.ReceivedCalls());
    }

    [Fact]
    public async Task OpenedFileIdentityMismatchIsReportedAsChangedEvenWhenSizeAndTimestampMatch()
    {
        var fileSystem = RootedFileSystem();
        var file = FileAt(RootPath, "replaced.txt", "first");
        var discovered = file.Snapshot with
                             {
                                 Identity = new DirectoryEntryIdentity(VolumeId: 7,
                                                                       FileIdHigh: 0,
                                                                       FileIdLow: 11)
                             };
        var replacement = discovered with
                              {
                                  Identity = new DirectoryEntryIdentity(VolumeId: 7,
                                                                        FileIdHigh: 0,
                                                                        FileIdLow: 12)
                              };
        fileSystem.SetEnumeration(RootPath, Enumeration(discovered));
        fileSystem.SetRead(discovered.FullPath,
                           new StableFileReadResult(file.Read.Content,
                                                    replacement,
                                                    replacement,
                                                    string.Empty,
                                                    null));
        var intake = SuccessfulIntake();
        var scanner = MakeScanner(fileSystem, intake, CandidateRepository());

        var report = await scanner.ScanAsync(Request(recursive: false), TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanReasonCodes.FileChangedDuringScan, Assert.Single(report.Entries).ReasonCode);
        Assert.Empty(intake.ReceivedCalls());
    }

    [Fact]
    public async Task HandleResolvedPathOutsideRootIsSkippedBeforeTheFileIsOpened()
    {
        var fileSystem = RootedFileSystem();
        var file = FileAt(RootPath, "redirected.txt", "unsafe");
        var redirected = file.Snapshot with
                             {
                                 Identity = new DirectoryEntryIdentity(VolumeId: 7,
                                                                       FileIdHigh: 0,
                                                                       FileIdLow: 21),
                                 ResolvedPath = Path.Combine(OutsideRootPath, "redirected.txt")
                             };
        fileSystem.SetEnumeration(RootPath, Enumeration(redirected));
        var intake = SuccessfulIntake();
        var scanner = MakeScanner(fileSystem, intake, CandidateRepository());

        var report = await scanner.ScanAsync(Request(recursive: false), TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanReasonCodes.PathOutsideRoot, Assert.Single(report.Entries).ReasonCode);
        Assert.Empty(fileSystem.ReadPaths);
        Assert.Empty(intake.ReceivedCalls());
    }

    [Fact]
    public async Task UnsupportedOversizeReparseAndEscapedEntriesAreSkippedWithoutOpeningThem()
    {
        var fileSystem = RootedFileSystem();
        var unsupported = FileAt(RootPath, "image.png", "png");
        var oversize = FileAt(RootPath, "large.pdf", "large").Snapshot with { ByteLength = MaxFileBytes + 1 };
        var linkedFile = FileAt(RootPath, "linked.txt", "linked").Snapshot with
                             {
                                 Attributes = FileAttributes.ReparsePoint
                             };
        var linkedDirectory = DirectoryAt(RootPath, "linked-directory") with
                                  {
                                      Attributes = FileAttributes.Directory | FileAttributes.ReparsePoint
                                  };
        var escaped = new DirectoryEntrySnapshot(Path.Combine(OutsideRootPath, "escape.md"),
                                                 FileAttributes.Normal,
                                                 4,
                                                 SourceTime);
        fileSystem.SetEnumeration(RootPath,
                                  Enumeration(unsupported.Snapshot,
                                              oversize,
                                              linkedFile,
                                              linkedDirectory,
                                              escaped));
        var scanner = MakeScanner(fileSystem, SuccessfulIntake(), CandidateRepository());

        var report = await scanner.ScanAsync(Request(recursive: true), TestContext.Current.CancellationToken);

        Assert.Equal([DirectoryScanReasonCodes.PathOutsideRoot,
                      DirectoryScanReasonCodes.FileUnsupportedType,
                      DirectoryScanReasonCodes.FileTooLarge,
                      DirectoryScanReasonCodes.DirectoryReparsePointSkipped,
                      DirectoryScanReasonCodes.FileReparsePointSkipped],
                     report.Entries.Select(entry => entry.ReasonCode));
        Assert.Empty(fileSystem.ReadPaths);
        Assert.DoesNotContain(linkedDirectory.FullPath,
                              fileSystem.EnumeratedPaths,
                              StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DirectoryScanReasonCodes.DirectoryAccessDenied)]
    [InlineData(DirectoryScanReasonCodes.DirectoryDisappeared)]
    [InlineData(DirectoryScanReasonCodes.DirectoryIoError)]
    public async Task RecursiveDirectoryEnumerationFailuresRemainDistinct(string reasonCode)
    {
        var fileSystem = RootedFileSystem();
        var nestedDirectory = DirectoryAt(RootPath, "nested");
        fileSystem.SetEnumeration(RootPath, Enumeration(nestedDirectory));
        fileSystem.SetEnumeration(nestedDirectory.FullPath,
                                  new DirectoryEnumerationResult([], reasonCode, new IOException("raw detail")));
        var scanner = MakeScanner(fileSystem, SuccessfulIntake(), CandidateRepository());

        var report = await scanner.ScanAsync(Request(recursive: true), TestContext.Current.CancellationToken);

        Assert.Equal(reasonCode, Assert.Single(report.Entries).ReasonCode);
        Assert.DoesNotContain("raw detail", JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CandidateMetadataUsesOnlyTheScanScopedPreviewWorkspaceAndNeverPublishesOrDeletesAVersion()
    {
        var fileSystem = RootedFileSystem();
        var file = FileAt(RootPath, "manual.pdf", "pdf");
        fileSystem.SetEnumeration(RootPath, Enumeration(file.Snapshot));
        fileSystem.SetRead(file.Snapshot.FullPath, file.Read);
        var repository = CandidateRepository();
        var scanner = MakeScanner(fileSystem, SuccessfulIntake(), repository);

        await scanner.ScanAsync(Request(recursive: false), TestContext.Current.CancellationToken);

        await repository.Received(requiredNumberOfCalls: 1)
                        .PersistRevisionAsync(Arg.Is<DocumentRevisionRecord>(revision =>
                                                                                revision != null
                                                                                && revision.ScanRunId == ScanRunId
                                                                                && revision.State == DocumentRevisionState.Candidate
                                                                                && revision.Version == "preview"
                                                                                && !revision.LibraryId.Equals(LibraryId,
                                                                                                              StringComparison.Ordinal)),
                                              Arg.Any<Stream>(),
                                              Arg.Any<Stream?>(),
                                              Arg.Any<CancellationToken>());
        await repository.DidNotReceiveWithAnyArgs()
                        .DeleteVersionAsync(default!,
                                            default!,
                                            TestContext.Current.CancellationToken);
        await repository.DidNotReceiveWithAnyArgs()
                        .SetRevisionStateAsync(default!,
                                               default!,
                                               default,
                                               TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FatalFailureReturnsSanitizedFailureAndStillCleansCandidates()
    {
        var fileSystem = RootedFileSystem();
        var file = FileAt(RootPath, "manual.txt", "text");
        fileSystem.SetEnumeration(RootPath, Enumeration(file.Snapshot));
        fileSystem.SetRead(file.Snapshot.FullPath, file.Read);
        var intake = Substitute.For<IDocumentIntake>();
        intake.ReadAsync(Arg.Any<DocumentIntakeRequest>(), Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromException<DocumentIntakeResult>(
                           new InvalidOperationException($"raw failure at {RootPath}")));
        var repository = CandidateRepository();
        var scanner = MakeScanner(fileSystem, intake, repository);

        var report = await scanner.ScanAsync(Request(recursive: false), TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanStatus.Failed, report.Status);
        Assert.Equal(DirectoryScanReasonCodes.ScanFailed, report.ReasonCode);
        Assert.DoesNotContain(RootPath, JsonSerializer.Serialize(report), StringComparison.OrdinalIgnoreCase);
        await AssertCleanupReceivedAsync(repository);
    }

    [Fact]
    public async Task CancellationIsRethrownOnlyAfterCandidateCleanupWithANonCancelledToken()
    {
        var fileSystem = RootedFileSystem();
        var file = FileAt(RootPath, "manual.txt", "text");
        fileSystem.SetEnumeration(RootPath, Enumeration(file.Snapshot));
        fileSystem.SetRead(file.Snapshot.FullPath, file.Read);
        var intake = Substitute.For<IDocumentIntake>();
        intake.ReadAsync(Arg.Any<DocumentIntakeRequest>(), Arg.Any<CancellationToken>())
              .Returns(call => Task.FromCanceled<DocumentIntakeResult>(call.Arg<CancellationToken>()));
        var repository = CandidateRepository();
        var scanner = MakeScanner(fileSystem, intake, repository);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync(Request(recursive: false),
                                                                                         cancellation.Token));

        await AssertCleanupReceivedAsync(repository);
    }

    private static ScriptedDirectoryScanFileSystem RootedFileSystem()
    {
        var result = new ScriptedDirectoryScanFileSystem();
        result.SetInspection(RootPath,
                             new DirectoryPathResult(new DirectoryEntrySnapshot(RootPath,
                                                                                FileAttributes.Directory,
                                                                                0,
                                                                                SourceTime),
                                                     string.Empty,
                                                     null));
        return result;
    }

    private static IDocumentIntake SuccessfulIntake()
    {
        var result = Substitute.For<IDocumentIntake>();
        result.ReadAsync(Arg.Any<DocumentIntakeRequest>(), Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult(new DocumentIntakeResult(true,
                                                                     DocumentIntakeReasonCodes.Extracted,
                                                                     "The document was extracted.",
                                                                     "Guide",
                                                                     [new DocumentIntakeSection(0,
                                                                                                "Guide",
                                                                                                "content",
                                                                                                null,
                                                                                                null)],
                                                                     "{\"sections\":[]}"u8.ToArray(),
                                                                     "application/json",
                                                                     new DocumentExtractionProvenance
                                                                         {
                                                                             ExtractorName = "test",
                                                                             ExtractorVersion = "1"
                                                                         })));
        return result;
    }

    private static ISourceDocumentRepository CandidateRepository()
    {
        var result = Substitute.For<ISourceDocumentRepository>();
        result.GetOrCreateDocumentAsync(Arg.Any<SourceDocumentRecord>(), Arg.Any<CancellationToken>())
              .Returns(call => Task.FromResult(call.Arg<SourceDocumentRecord>()!));
        result.PersistRevisionAsync(Arg.Any<DocumentRevisionRecord>(),
                                    Arg.Any<Stream>(),
                                    Arg.Any<Stream?>(),
                                    Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);
        result.DeleteCandidateScanRunAsync(Arg.Any<string>(),
                                           Arg.Any<string>(),
                                           Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(1L));
        return result;
    }

    private static DirectoryScanner MakeScanner(IDirectoryScanFileSystem fileSystem,
                                                IDocumentIntake intake,
                                                ISourceDocumentRepository repository) =>
        new(fileSystem,
            intake,
            repository,
            NullLogger<DirectoryScanner>.Instance,
            new FixedDirectoryScanTimeProvider(ScanTime));

    private static DirectoryScanRequest Request(bool recursive) => new()
        {
            LibraryId = LibraryId,
            ScanRunId = ScanRunId,
            RootPath = RootPath,
            Recursive = recursive,
            MaxFileBytes = MaxFileBytes
        };

    private static (DirectoryEntrySnapshot Snapshot, StableFileReadResult Read) FileAt(string directory,
                                                                                       string name,
                                                                                       string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var snapshot = new DirectoryEntrySnapshot(Path.Combine(directory, name),
                                                  FileAttributes.Normal,
                                                  bytes.Length,
                                                  SourceTime);
        var read = new StableFileReadResult(bytes, snapshot, snapshot, string.Empty, null);
        return (snapshot, read);
    }

    private static DirectoryEntrySnapshot DirectoryAt(string parent, string name) =>
        new(Path.Combine(parent, name), FileAttributes.Directory, 0, SourceTime);

    private static DirectoryEnumerationResult Enumeration(params DirectoryEntrySnapshot[] entries) =>
        new(entries, string.Empty, null);

    private static async Task AssertEphemeralCandidateLifecycleAsync(ISourceDocumentRepository repository)
    {
        await repository.Received()
                        .GetOrCreateDocumentAsync(Arg.Is<SourceDocumentRecord>(document =>
                                                                                 document != null
                                                                                 && document.LibraryId != LibraryId
                                                                                 && document.LibraryId.Contains(
                                                                                     ScanRunId,
                                                                                     StringComparison.Ordinal)
                                                                                 && document.FirstSeenVersion
                                                                                     == "preview"),
                                                  Arg.Any<CancellationToken>());
        await AssertCleanupReceivedAsync(repository);
    }

    private static async Task AssertCleanupReceivedAsync(ISourceDocumentRepository repository)
    {
        await repository.Received(requiredNumberOfCalls: 1)
                        .DeleteCandidateScanRunAsync(Arg.Is<string>(workspace => workspace != null
                                                                                && workspace != LibraryId
                                                                                && workspace.Contains(
                                                                                    ScanRunId,
                                                                                    StringComparison.Ordinal)),
                                                     ScanRunId,
                                                     CancellationToken.None);
    }

    private sealed class FixedDirectoryScanTimeProvider : TimeProvider
    {
        public FixedDirectoryScanTimeProvider(DateTimeOffset utcNow)
        {
            mUtcNow = utcNow;
        }

        private readonly DateTimeOffset mUtcNow;

        public override DateTimeOffset GetUtcNow() => mUtcNow;
    }

    private static readonly DateTime SourceTime = new(year: 2026,
                                                      month: 8,
                                                      day: 4,
                                                      hour: 11,
                                                      minute: 0,
                                                      second: 0,
                                                      DateTimeKind.Utc);
    private static readonly DateTimeOffset ScanTime = new(year: 2026,
                                                          month: 8,
                                                          day: 4,
                                                          hour: 12,
                                                          minute: 0,
                                                          second: 0,
                                                          TimeSpan.Zero);
    private static readonly string TestRoot = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "saddlerag-scripted-tests"));
    private static readonly string RootPath = Path.Combine(TestRoot, "manuals");
    private static readonly string OutsideRootPath = Path.Combine(TestRoot, "outside");
    private const string LibraryId = "manual-library";
    private const string ScanRunId = "scan-run-123";
    private const long MaxFileBytes = 1024;
}
