// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text;
using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Documents.Intake;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>
///     Shared safe-acquisition engine for explicit directory previews and
///     publishing scans. The caller-supplied sink owns all persistence.
/// </summary>
public sealed class DirectoryScanEngine
{
    public DirectoryScanEngine(IDirectoryScanFileSystem fileSystem,
                               IDocumentIntake documentIntake,
                               ILogger<DirectoryScanEngine> logger,
                               TimeProvider timeProvider)
        : this(fileSystem, documentIntake, logger, timeProvider, sharedLoggerCategory: true)
    {
    }

    internal DirectoryScanEngine(IDirectoryScanFileSystem fileSystem,
                                 IDocumentIntake documentIntake,
                                 ILogger logger,
                                 TimeProvider timeProvider,
                                 bool sharedLoggerCategory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(documentIntake);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        mFileSystem = fileSystem;
        mDocumentIntake = documentIntake;
        mLogger = logger;
        mTimeProvider = timeProvider;
        _ = sharedLoggerCategory;
    }

    private readonly IDocumentIntake mDocumentIntake;
    private readonly IDirectoryScanFileSystem mFileSystem;
    private readonly ILogger mLogger;
    private readonly TimeProvider mTimeProvider;

    public async Task<DirectoryScanReport> ScanAsync(DirectoryScanRequest request,
                                                     IDirectoryScanSink sink,
                                                     Action<DirectoryScanProgress>? onProgress = null,
                                                     CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        DateTime startedAtUtc = mTimeProvider.GetUtcNow().UtcDateTime;
        var validator = new DirectoryRootValidator(mFileSystem);
        DirectoryRootValidationResult root = validator.Validate(request.RootPath);
        DirectoryScanReport result;
        if (!root.Succeeded)
            result = RootFailureReport(request, root, startedAtUtc);
        else
        {
            result = await ScanValidatedRootAsync(request,
                                                  sink,
                                                  root.CanonicalRoot,
                                                  startedAtUtc,
                                                  onProgress,
                                                  cancellationToken);
        }

        return result;
    }

    private async Task<DirectoryScanReport> ScanValidatedRootAsync(
        DirectoryScanRequest request,
        IDirectoryScanSink sink,
        string canonicalRoot,
        DateTime startedAtUtc,
        Action<DirectoryScanProgress>? onProgress,
        CancellationToken ct)
    {
        var entries = new List<DirectoryScanEntryResult>();
        IReadOnlyList<DiscoveredFile> files = DiscoverFiles(request, canonicalRoot, entries, ct);
        int supportedDocuments = files.Count(file => IsSupported(request, file.CanonicalPath));
        var progress = new DirectoryScanProgress(files.Count, supportedDocuments, 0, null);
        onProgress?.Invoke(progress);

        foreach(DiscoveredFile file in files.OrderBy(item => item.NormalizedRelativePath,
                                                       StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            FileProcessingResult processed = await ProcessFileAsync(request, sink, canonicalRoot, file, ct);
            entries.Add(processed.Entry);
            if (processed.Completed)
            {
                progress = progress with
                               {
                                   DocumentsCompleted = progress.DocumentsCompleted + 1,
                                   CurrentRelativePath = file.NormalizedRelativePath
                               };
            }
            else
            {
                if (IsSupported(request, file.CanonicalPath))
                    progress = progress with { CurrentRelativePath = file.NormalizedRelativePath };
            }
            onProgress?.Invoke(progress);
        }

        DirectoryScanReport result = CompleteReport(request, startedAtUtc, entries);
        onProgress?.Invoke(progress);
        return result;
    }

    private IReadOnlyList<DiscoveredFile> DiscoverFiles(DirectoryScanRequest request,
                                                        string canonicalRoot,
                                                        List<DirectoryScanEntryResult> results,
                                                        CancellationToken ct)
    {
        var files = new List<DiscoveredFile>();
        var pending = new Stack<string>();
        pending.Push(canonicalRoot);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            DirectoryEnumerationResult enumeration = mFileSystem.EnumerateDirectory(directory);
            if (!enumeration.Succeeded)
                AddDirectoryFailure(results, canonicalRoot, directory, enumeration);
            else
            {
                IEnumerable<DirectoryEntrySnapshot> ordered = enumeration.Entries.OrderBy(
                    entry => entry.FullPath,
                    StringComparer.OrdinalIgnoreCase);
                foreach(DirectoryEntrySnapshot entry in ordered)
                    DiscoverEntry(request, canonicalRoot, entry, pending, files, results);
            }
        }

        return files;
    }

    private void DiscoverEntry(DirectoryScanRequest request,
                               string canonicalRoot,
                               DirectoryEntrySnapshot entry,
                               Stack<string> pending,
                               List<DiscoveredFile> files,
                               List<DirectoryScanEntryResult> results)
    {
        string? canonicalPath = TryCanonicalPath(entry.FullPath);
        if (canonicalPath == null || !IsContained(canonicalRoot, canonicalPath))
        {
            results.Add(EntryResult(SafeOutsideRelativePath(entry.FullPath),
                                    EntryKind(entry),
                                    DirectoryScanEntryStatus.Skipped,
                                    DirectoryScanReasonCodes.PathOutsideRoot,
                                    entry.ByteLength));
        }
        else
            DiscoverContainedEntry(request, canonicalRoot, canonicalPath, entry, pending, files, results);
    }

    private void DiscoverContainedEntry(DirectoryScanRequest request,
                                        string canonicalRoot,
                                        string canonicalPath,
                                        DirectoryEntrySnapshot entry,
                                        Stack<string> pending,
                                        List<DiscoveredFile> files,
                                        List<DirectoryScanEntryResult> results)
    {
        string displayRelativePath = DisplayRelativePath(canonicalRoot, canonicalPath);
        string normalizedRelativePath = displayRelativePath.ToLowerInvariant();
        bool isDirectory = entry.Attributes.HasFlag(FileAttributes.Directory);
        bool isReparsePoint = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
        switch(IsExcluded(request, normalizedRelativePath), isReparsePoint, isDirectory)
        {
            case (true, _, _):
                mLogger.LogDebug("Directory scan excluded {RelativePath} by its registered pattern.",
                                 normalizedRelativePath);
                break;
            case (_, true, _):
                string reasonCode = isDirectory
                    ? DirectoryScanReasonCodes.DirectoryReparsePointSkipped
                    : DirectoryScanReasonCodes.FileReparsePointSkipped;
                results.Add(EntryResult(normalizedRelativePath,
                                        EntryKind(entry),
                                        DirectoryScanEntryStatus.Skipped,
                                        reasonCode,
                                        entry.ByteLength));
                break;
            case (_, _, true):
                if (request.Recursive)
                    pending.Push(canonicalPath);
                break;
            default:
                files.Add(new DiscoveredFile(canonicalPath,
                                             normalizedRelativePath,
                                             displayRelativePath,
                                             entry));
                break;
        }
    }

    private string? TryCanonicalPath(string fullPath)
    {
        string? result;
        try
        {
            result = Path.GetFullPath(fullPath);
        }
        catch(ArgumentException ex)
        {
            mLogger.LogWarning(ex, "A directory-scan entry had an invalid path.");
            result = null;
        }
        catch(NotSupportedException ex)
        {
            mLogger.LogWarning(ex, "A directory-scan entry had an unsupported path.");
            result = null;
        }

        return result;
    }

    private async Task<FileProcessingResult> ProcessFileAsync(DirectoryScanRequest request,
                                                              IDirectoryScanSink sink,
                                                              string canonicalRoot,
                                                              DiscoveredFile file,
                                                              CancellationToken ct)
    {
        string extension = Path.GetExtension(file.CanonicalPath);
        FileProcessingResult result;
        if (!IsSupported(request, file.CanonicalPath) ||
            !smMediaTypes.TryGetValue(extension, out string? mediaType))
        {
            result = FileProcessingResult.Skipped(EntryResult(file.NormalizedRelativePath,
                                                              DirectoryScanEntryKind.File,
                                                              DirectoryScanEntryStatus.Skipped,
                                                              DirectoryScanReasonCodes.FileUnsupportedType,
                                                              file.Snapshot.ByteLength));
        }
        else
        {
            if (file.Snapshot.ByteLength > request.MaxFileBytes)
            {
                result = FileProcessingResult.Skipped(EntryResult(file.NormalizedRelativePath,
                                                                  DirectoryScanEntryKind.File,
                                                                  DirectoryScanEntryStatus.Skipped,
                                                                  DirectoryScanReasonCodes.FileTooLarge,
                                                                  file.Snapshot.ByteLength));
            }
            else
            {
                StableFileReadResult read = await mFileSystem.ReadStableFileAsync(file.CanonicalPath,
                                                                                   request.MaxFileBytes,
                                                                                   ct);
                result = read.Succeeded
                    ? await ProcessStableReadAsync(sink, canonicalRoot, file, mediaType, read, ct)
                    : FileReadFailure(file, read);
            }
        }

        return result;
    }

    private FileProcessingResult FileReadFailure(DiscoveredFile file, StableFileReadResult read)
    {
        LogExpectedFilesystemFailure(read.Error, file.NormalizedRelativePath, read.ReasonCode);
        DirectoryScanEntryResult entry = EntryResult(file.NormalizedRelativePath,
                                                     DirectoryScanEntryKind.File,
                                                     FailureStatus(read.ReasonCode),
                                                     NormalizeFileReadReason(read.ReasonCode),
                                                     file.Snapshot.ByteLength);
        return FileProcessingResult.Skipped(entry);
    }

    private async Task<FileProcessingResult> ProcessStableReadAsync(IDirectoryScanSink sink,
                                                                    string canonicalRoot,
                                                                    DiscoveredFile file,
                                                                    string mediaType,
                                                                    StableFileReadResult read,
                                                                    CancellationToken ct)
    {
        DirectoryScanEntryResult? terminal = StableReadTerminalResult(canonicalRoot,
                                                                       file.CanonicalPath,
                                                                       file.NormalizedRelativePath,
                                                                       file.Snapshot,
                                                                       read);
        FileProcessingResult result;
        if (terminal != null)
            result = FileProcessingResult.Skipped(terminal);
        else
        {
            DirectoryEntrySnapshot before = read.Before
                ?? throw new InvalidDataException("A successful stable read is missing its initial metadata.");
            var fingerprintProvider = mDocumentIntake as IDocumentIntakeFingerprintProvider;
            DocumentExtractionFingerprint? fingerprint = fingerprintProvider?.GetFingerprint(
                file.DisplayRelativePath,
                mediaType);
            var stable = new DirectoryStableDocument(file.NormalizedRelativePath,
                                                     file.DisplayRelativePath,
                                                     mediaType,
                                                     before,
                                                     read.Content)
                             {
                                 ExtractionFingerprint = fingerprint
                             };
            int? reusedSectionCount = await TryReuseAsync(sink, stable, ct);
            result = reusedSectionCount.HasValue
                ? Extracted(file, reusedSectionCount.Value)
                : await IntakeAsync(sink, stable, file, ct);
        }

        return result;
    }

    private static async Task<int?> TryReuseAsync(IDirectoryScanSink sink,
                                                  DirectoryStableDocument stable,
                                                  CancellationToken ct)
    {
        int? result = null;
        if (sink is IDirectoryScanReuseSink reusable)
            result = await reusable.TryAcceptUnchangedAsync(stable, ct);
        return result;
    }

    private async Task<FileProcessingResult> IntakeAsync(IDirectoryScanSink sink,
                                                         DirectoryStableDocument stable,
                                                         DiscoveredFile file,
                                                         CancellationToken ct)
    {
        var intakeRequest = new DocumentIntakeRequest(Path.GetFileName(stable.DisplayRelativePath),
                                                      stable.DisplayRelativePath,
                                                      stable.MediaType,
                                                      stable.Content);
        DocumentIntakeResult intake = await mDocumentIntake.ReadAsync(intakeRequest, ct);
        FileProcessingResult result;
        if (!intake.Succeeded)
        {
            result = FileProcessingResult.Skipped(new DirectoryScanEntryResult(
                                                      stable.NormalizedRelativePath,
                                                      DirectoryScanEntryKind.File,
                                                      DirectoryScanEntryStatus.Failed,
                                                      intake.ReasonCode,
                                                      intake.Detail,
                                                      0,
                                                      stable.Source.ByteLength));
        }
        else
        {
            await sink.AcceptAsync(new DirectoryAcquiredDocument(stable, intake), ct);
            result = Extracted(file, intake.Sections.Count);
        }

        return result;
    }

    private static FileProcessingResult Extracted(DiscoveredFile file, int sectionCount) =>
        new(EntryResult(file.NormalizedRelativePath,
                        DirectoryScanEntryKind.File,
                        DirectoryScanEntryStatus.Extracted,
                        DirectoryScanReasonCodes.FileExtracted,
                        file.Snapshot.ByteLength,
                        sectionCount),
            Completed: true);

    private void AddDirectoryFailure(List<DirectoryScanEntryResult> results,
                                     string canonicalRoot,
                                     string directory,
                                     DirectoryEnumerationResult enumeration)
    {
        string relativePath = NormalizeRelativePath(canonicalRoot, directory);
        LogExpectedFilesystemFailure(enumeration.Error, relativePath, enumeration.ReasonCode);
        results.Add(EntryResult(relativePath,
                                string.IsNullOrEmpty(relativePath)
                                    ? DirectoryScanEntryKind.Root
                                    : DirectoryScanEntryKind.Directory,
                                DirectoryScanEntryStatus.Failed,
                                NormalizeDirectoryReason(enumeration.ReasonCode),
                                0));
    }

    private void LogExpectedFilesystemFailure(Exception? error, string relativePath, string reasonCode)
    {
        if (error != null)
        {
            mLogger.LogWarning(error,
                               "Directory scan skipped {RelativePath} with reason {ReasonCode}.",
                               relativePath,
                               reasonCode);
        }
    }

    private DirectoryScanReport CompleteReport(DirectoryScanRequest request,
                                               DateTime startedAtUtc,
                                               IReadOnlyList<DirectoryScanEntryResult> entries)
    {
        IReadOnlyList<DirectoryScanEntryResult> ordered = entries.OrderBy(entry => entry.RelativePath,
                                                                          StringComparer.Ordinal)
                                                                 .ThenBy(entry => entry.Kind)
                                                                 .ThenBy(entry => entry.ReasonCode,
                                                                         StringComparer.Ordinal)
                                                                 .ToArray();
        int failed = ordered.Count(entry => entry.Status == DirectoryScanEntryStatus.Failed);
        DirectoryScanStatus status = failed > 0
            ? DirectoryScanStatus.CompletedWithErrors
            : DirectoryScanStatus.Completed;
        string reasonCode = failed > 0
            ? DirectoryScanReasonCodes.ScanCompletedWithErrors
            : DirectoryScanReasonCodes.ScanCompleted;
        string detail = failed > 0 ? CompletedWithErrorsDetail : CompletedDetail;
        return MakeReport(request, startedAtUtc, status, reasonCode, detail, ordered);
    }

    private DirectoryScanReport RootFailureReport(DirectoryScanRequest request,
                                                  DirectoryRootValidationResult root,
                                                  DateTime startedAtUtc)
    {
        var entry = new DirectoryScanEntryResult(string.Empty,
                                                 DirectoryScanEntryKind.Root,
                                                 DirectoryScanEntryStatus.Failed,
                                                 root.ReasonCode,
                                                 root.Detail,
                                                 0,
                                                 0);
        return MakeReport(request,
                          startedAtUtc,
                          DirectoryScanStatus.Failed,
                          DirectoryScanReasonCodes.ScanFailed,
                          RootFailedDetail,
                          [entry]);
    }

    private DirectoryScanReport MakeReport(DirectoryScanRequest request,
                                           DateTime startedAtUtc,
                                           DirectoryScanStatus status,
                                           string reasonCode,
                                           string detail,
                                           IReadOnlyList<DirectoryScanEntryResult> entries) =>
        new()
            {
                LibraryId = request.LibraryId,
                ScanRunId = request.ScanRunId,
                Status = status,
                ReasonCode = reasonCode,
                Detail = detail,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = mTimeProvider.GetUtcNow().UtcDateTime,
                Entries = entries,
                DiscoveredCount = entries.Count,
                ExtractedCount = entries.Count(entry => entry.Status == DirectoryScanEntryStatus.Extracted),
                SkippedCount = entries.Count(entry => entry.Status == DirectoryScanEntryStatus.Skipped),
                FailedCount = entries.Count(entry => entry.Status == DirectoryScanEntryStatus.Failed)
            };

    private static DirectoryScanEntryResult EntryResult(string relativePath,
                                                        DirectoryScanEntryKind kind,
                                                        DirectoryScanEntryStatus status,
                                                        string reasonCode,
                                                        long byteLength,
                                                        int sectionCount = 0) =>
        new(relativePath, kind, status, reasonCode, DetailFor(reasonCode), sectionCount, byteLength);

    private static string DetailFor(string reasonCode) =>
        reasonCode switch
            {
                DirectoryScanReasonCodes.FileExtracted => FileExtractedDetail,
                DirectoryScanReasonCodes.FileUnsupportedType => UnsupportedFileDetail,
                DirectoryScanReasonCodes.FileTooLarge => FileTooLargeDetail,
                DirectoryScanReasonCodes.FileEmpty => EmptyFileDetail,
                DirectoryScanReasonCodes.FileAccessDenied => FileAccessDeniedDetail,
                DirectoryScanReasonCodes.FileLocked => FileLockedDetail,
                DirectoryScanReasonCodes.FileDisappeared => FileDisappearedDetail,
                DirectoryScanReasonCodes.FileChangedDuringScan => FileChangedDetail,
                DirectoryScanReasonCodes.FileReparsePointSkipped => FileReparsePointDetail,
                DirectoryScanReasonCodes.DirectoryAccessDenied => DirectoryAccessDeniedDetail,
                DirectoryScanReasonCodes.DirectoryDisappeared => DirectoryDisappearedDetail,
                DirectoryScanReasonCodes.DirectoryReparsePointSkipped => DirectoryReparsePointDetail,
                DirectoryScanReasonCodes.PathOutsideRoot => PathOutsideRootDetail,
                _ => FileSystemIoErrorDetail
            };

    private static DirectoryScanEntryStatus FailureStatus(string reasonCode) =>
        reasonCode is DirectoryScanReasonCodes.FileTooLarge
            or DirectoryScanReasonCodes.FileReparsePointSkipped
            ? DirectoryScanEntryStatus.Skipped
            : DirectoryScanEntryStatus.Failed;

    private static string NormalizeFileReadReason(string reasonCode) =>
        reasonCode switch
            {
                DirectoryScanReasonCodes.FileAccessDenied => reasonCode,
                DirectoryScanReasonCodes.FileLocked => reasonCode,
                DirectoryScanReasonCodes.FileDisappeared => reasonCode,
                DirectoryScanReasonCodes.FileTooLarge => reasonCode,
                DirectoryScanReasonCodes.FileReparsePointSkipped => reasonCode,
                _ => DirectoryScanReasonCodes.FileIoError
            };

    private static string NormalizeDirectoryReason(string reasonCode) =>
        reasonCode switch
            {
                DirectoryScanReasonCodes.DirectoryAccessDenied => reasonCode,
                DirectoryScanReasonCodes.DirectoryDisappeared => reasonCode,
                _ => DirectoryScanReasonCodes.DirectoryIoError
            };

    private static DirectoryScanEntryResult? StableReadTerminalResult(string canonicalRoot,
                                                                      string canonicalPath,
                                                                      string relativePath,
                                                                      DirectoryEntrySnapshot discovered,
                                                                      StableFileReadResult read)
    {
        string? reasonCode;
        DirectoryEntrySnapshot? before = read.Before;
        DirectoryEntrySnapshot? after = read.After;
        if (before == null || after == null)
            reasonCode = DirectoryScanReasonCodes.FileIoError;
        else
        {
            bool pathsAreContained = SnapshotPathsAreContained(canonicalRoot,
                                                                canonicalPath,
                                                                before,
                                                                after);
            bool isReparsePoint = before.Attributes.HasFlag(FileAttributes.ReparsePoint)
                                  || after.Attributes.HasFlag(FileAttributes.ReparsePoint);
            bool isStable = SnapshotsAreStable(discovered, before, after, read.Content.Length);
            reasonCode = (pathsAreContained, isReparsePoint, isStable, read.Content.IsEmpty) switch
                {
                    (false, _, _, _) => DirectoryScanReasonCodes.PathOutsideRoot,
                    (_, true, _, _) => DirectoryScanReasonCodes.FileReparsePointSkipped,
                    (_, _, false, _) => DirectoryScanReasonCodes.FileChangedDuringScan,
                    (_, _, _, true) => DirectoryScanReasonCodes.FileEmpty,
                    _ => null
                };
        }

        DirectoryScanEntryResult? result = null;
        if (reasonCode != null)
        {
            DirectoryScanEntryStatus status = reasonCode is DirectoryScanReasonCodes.PathOutsideRoot
                or DirectoryScanReasonCodes.FileReparsePointSkipped
                or DirectoryScanReasonCodes.FileEmpty
                ? DirectoryScanEntryStatus.Skipped
                : DirectoryScanEntryStatus.Failed;
            result = EntryResult(relativePath,
                                 DirectoryScanEntryKind.File,
                                 status,
                                 reasonCode,
                                 after?.ByteLength ?? discovered.ByteLength);
        }

        return result;
    }

    private static bool SnapshotsAreStable(DirectoryEntrySnapshot discovered,
                                           DirectoryEntrySnapshot before,
                                           DirectoryEntrySnapshot after,
                                           int bytesRead) =>
        discovered.ByteLength == before.ByteLength
        && before.ByteLength == after.ByteLength
        && after.ByteLength == bytesRead
        && discovered.LastWriteTimeUtc == before.LastWriteTimeUtc
        && before.LastWriteTimeUtc == after.LastWriteTimeUtc;

    private static bool SnapshotPathsAreContained(string canonicalRoot,
                                                  string expectedPath,
                                                  DirectoryEntrySnapshot before,
                                                  DirectoryEntrySnapshot after)
    {
        bool result;
        try
        {
            string beforePath = Path.GetFullPath(before.FullPath);
            string afterPath = Path.GetFullPath(after.FullPath);
            result = IsContained(canonicalRoot, beforePath)
                     && IsContained(canonicalRoot, afterPath)
                     && beforePath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase)
                     && afterPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase);
        }
        catch(ArgumentException)
        {
            result = false;
        }
        catch(NotSupportedException)
        {
            result = false;
        }

        return result;
    }

    private static bool IsContained(string canonicalRoot, string candidate)
    {
        string rootPrefix = canonicalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupported(DirectoryScanRequest request, string canonicalPath)
    {
        string extension = Path.GetExtension(canonicalPath);
        bool result = smMediaTypes.ContainsKey(extension);
        if (result && request.AllowedExtensions.Count > 0)
        {
            result = request.AllowedExtensions.Any(allowed =>
                                                       NormalizeExtension(allowed).Equals(extension,
                                                                                          StringComparison.OrdinalIgnoreCase));
        }

        return result;
    }

    private static bool IsExcluded(DirectoryScanRequest request, string normalizedRelativePath) =>
        request.ExclusionPatterns.Any(pattern =>
                                            System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                                                pattern.Replace('\\', '/'),
                                                normalizedRelativePath,
                                                ignoreCase: true));

    private static string NormalizeExtension(string extension)
    {
        string trimmed = extension.Trim();
        return trimmed.StartsWith('.') ? trimmed : $".{trimmed}";
    }

    private static DirectoryScanEntryKind EntryKind(DirectoryEntrySnapshot entry) =>
        entry.Attributes.HasFlag(FileAttributes.Directory)
            ? DirectoryScanEntryKind.Directory
            : DirectoryScanEntryKind.File;

    private static string NormalizeRelativePath(string canonicalRoot, string fullPath) =>
        DisplayRelativePath(canonicalRoot, fullPath).ToLowerInvariant();

    private static string DisplayRelativePath(string canonicalRoot, string fullPath)
    {
        string relativePath = Path.GetRelativePath(canonicalRoot, fullPath)
                                  .Replace('\\', '/')
                                  .Normalize(NormalizationForm.FormC);
        return relativePath.Equals(".", StringComparison.Ordinal) ? string.Empty : relativePath;
    }

    private static string SafeOutsideRelativePath(string fullPath)
    {
        string fileName = Path.GetFileName(fullPath);
        string result = string.IsNullOrWhiteSpace(fileName) ? OutsideRootFallbackName : fileName;
        return result.Replace('\\', '/')
                     .Normalize(NormalizationForm.FormC)
                     .ToLowerInvariant();
    }

    private static void ValidateRequest(DirectoryScanRequest request)
    {
        ArgumentException.ThrowIfNullOrEmpty(request.LibraryId);
        ArgumentException.ThrowIfNullOrEmpty(request.ScanRunId);
        ArgumentNullException.ThrowIfNull(request.RootPath);
        if (request.MaxFileBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "The file-size bound must be positive.");
    }

    private static readonly IReadOnlyDictionary<string, string> smMediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".md"] = "text/markdown",
                [".markdown"] = "text/markdown",
                [".txt"] = "text/plain",
                [".text"] = "text/plain",
                [".html"] = "text/html",
                [".htm"] = "text/html",
                [".pdf"] = "application/pdf",
                [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            };

    private sealed record DiscoveredFile(string CanonicalPath,
                                         string NormalizedRelativePath,
                                         string DisplayRelativePath,
                                         DirectoryEntrySnapshot Snapshot);

    private sealed record FileProcessingResult(DirectoryScanEntryResult Entry, bool Completed)
    {
        internal static FileProcessingResult Skipped(DirectoryScanEntryResult entry) => new(entry, Completed: false);
    }

    private const string CompletedDetail = "The directory scan completed successfully.";
    private const string CompletedWithErrorsDetail = "The directory scan completed with entry errors.";
    private const string RootFailedDetail = "The selected directory could not be scanned.";
    private const string FileExtractedDetail = "The file was extracted successfully.";
    private const string UnsupportedFileDetail = "The file type is not supported.";
    private const string FileTooLargeDetail = "The file exceeds the configured scan-size bound.";
    private const string EmptyFileDetail = "The file is empty.";
    private const string FileAccessDeniedDetail = "The file cannot be accessed.";
    private const string FileLockedDetail = "The file is locked.";
    private const string FileDisappearedDetail = "The file disappeared before it could be read.";
    private const string FileChangedDetail = "The file changed while it was being read.";
    private const string FileReparsePointDetail = "A linked or redirected file was skipped.";
    private const string DirectoryAccessDeniedDetail = "The directory cannot be accessed.";
    private const string DirectoryDisappearedDetail = "The directory disappeared before it could be enumerated.";
    private const string DirectoryReparsePointDetail = "A linked or redirected directory was skipped.";
    private const string PathOutsideRootDetail = "An entry resolving outside the selected root was skipped.";
    private const string FileSystemIoErrorDetail = "A filesystem input/output error prevented the entry from being scanned.";
    private const string OutsideRootFallbackName = "outside-root";
}
