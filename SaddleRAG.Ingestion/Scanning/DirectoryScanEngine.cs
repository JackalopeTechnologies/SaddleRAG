// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

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
        : this(fileSystem,
               documentIntake,
               logger,
               timeProvider,
               DirectoryPathIdentity.Platform,
               sharedLoggerCategory: true)
    {
    }

    internal DirectoryScanEngine(IDirectoryScanFileSystem fileSystem,
                                 IDocumentIntake documentIntake,
                                 ILogger logger,
                                 TimeProvider timeProvider,
                                 DirectoryPathIdentity pathIdentity,
                                 bool sharedLoggerCategory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(documentIntake);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(pathIdentity);
        mFileSystem = fileSystem;
        mDocumentIntake = documentIntake;
        mLogger = logger;
        mTimeProvider = timeProvider;
        mPathIdentity = pathIdentity;
        _ = sharedLoggerCategory;
    }

    private readonly IDocumentIntake mDocumentIntake;
    private readonly IDirectoryScanFileSystem mFileSystem;
    private readonly ILogger mLogger;
    private readonly DirectoryPathIdentity mPathIdentity;
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
            DirectoryEntrySnapshot rootSnapshot = root.Snapshot
                ?? throw new InvalidDataException("A validated directory root is missing its filesystem identity.");
            result = await ScanValidatedRootAsync(request,
                                                  sink,
                                                  root.CanonicalRoot,
                                                  rootSnapshot,
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
        DirectoryEntrySnapshot rootSnapshot,
        DateTime startedAtUtc,
        Action<DirectoryScanProgress>? onProgress,
        CancellationToken ct)
    {
        var entries = new List<DirectoryScanEntryResult>();
        DirectoryDiscoveryResult discovery = DiscoverFiles(request,
                                                           canonicalRoot,
                                                           rootSnapshot,
                                                           entries,
                                                           ct);
        IReadOnlyList<DiscoveredFile> files = discovery.Files;
        IReadOnlyList<DiscoveredFile> supported = files.Where(file => IsSupported(request, file.CanonicalPath))
                                                       .ToList();
        int supportedDocuments = supported.Count;
        var progress = new DirectoryScanProgress(files.Count, supportedDocuments, 0, null);
        onProgress?.Invoke(progress);
        DirectoryScanEntryResult? aggregateFailure = discovery.LimitFailure ??
                                                     AggregateDiscoveryFailure(request, supported);
        DirectoryScanReport result;
        if (aggregateFailure != null)
        {
            entries.Add(aggregateFailure);
            result = MakeReport(request,
                                startedAtUtc,
                                DirectoryScanStatus.Failed,
                                aggregateFailure.ReasonCode,
                                aggregateFailure.Detail,
                                entries);
        }
        else
        {
            var budget = new DirectoryScanBudget(request.MaxTotalBytes, request.MaxSectionCount);
            foreach(DiscoveredFile file in files.OrderBy(item => item.NormalizedRelativePath,
                                                          StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                FileProcessingResult processed = await ProcessFileAsync(request,
                                                                         sink,
                                                                         budget,
                                                                         canonicalRoot,
                                                                         file,
                                                                         ct);
                entries.Add(processed.Entry);
                progress = processed.Completed
                    ? progress with
                        {
                            DocumentsCompleted = progress.DocumentsCompleted + 1,
                            CurrentRelativePath = file.NormalizedRelativePath
                        }
                    : IsSupported(request, file.CanonicalPath)
                        ? progress with { CurrentRelativePath = file.NormalizedRelativePath }
                        : progress;
                onProgress?.Invoke(progress);
            }

            result = CompleteReport(request, startedAtUtc, entries);
        }

        onProgress?.Invoke(progress);
        return result;
    }

    private DirectoryDiscoveryResult DiscoverFiles(DirectoryScanRequest request,
                                                     string canonicalRoot,
                                                     DirectoryEntrySnapshot rootSnapshot,
                                                     List<DirectoryScanEntryResult> results,
                                                     CancellationToken ct)
    {
        var files = new List<DiscoveredFile>();
        var uniqueFiles = new HashSet<string>(mPathIdentity.Comparer);
        var pending = new Stack<DirectoryEntrySnapshot>();
        pending.Push(rootSnapshot);
        long directoryCount = 0;
        long entryCount = 0;
        long supportedCount = 0;
        long supportedBytes = 0;
        DirectoryScanEntryResult? limitFailure = null;
        while (pending.Count > 0 && limitFailure == null)
        {
            ct.ThrowIfCancellationRequested();
            DirectoryEntrySnapshot directorySnapshot = pending.Pop();
            string directory = Path.GetFullPath(directorySnapshot.FullPath);
            directoryCount++;
            if (directoryCount > request.MaxDirectoryCount)
            {
                limitFailure = DiscoveryLimitFailure(DirectoryScanReasonCodes.DirectoryCountLimitExceeded);
            }
            else
            {
                DirectoryEnumerationResult enumeration = mFileSystem.EnumerateDirectory(directory,
                                                                                         directorySnapshot);
                if (!enumeration.Succeeded)
                    AddDirectoryFailure(results, canonicalRoot, directory, enumeration);
                else
                {
                    limitFailure = DiscoverEnumeration(request,
                                                       canonicalRoot,
                                                       directory,
                                                       enumeration,
                                                       pending,
                                                       files,
                                                       uniqueFiles,
                                                       results,
                                                       ref entryCount,
                                                       ref supportedCount,
                                                       ref supportedBytes,
                                                       ct);
                }
            }
        }

        IReadOnlyList<DiscoveredFile> unique = RemoveIdentityCollisions(files, results);
        return new DirectoryDiscoveryResult(unique, limitFailure);
    }

    private DirectoryScanEntryResult? DiscoverEnumeration(
        DirectoryScanRequest request,
        string canonicalRoot,
        string directory,
        DirectoryEnumerationResult enumeration,
        Stack<DirectoryEntrySnapshot> pending,
        List<DiscoveredFile> files,
        HashSet<string> uniqueFiles,
        List<DirectoryScanEntryResult> results,
        ref long entryCount,
        ref long supportedCount,
        ref long supportedBytes,
        CancellationToken ct)
    {
        DirectoryScanEntryResult? result = null;
        try
        {
            foreach(DirectoryEntrySnapshot entry in enumeration.Entries)
            {
                ct.ThrowIfCancellationRequested();
                result = DiscoverEnumerationEntry(request,
                                                  canonicalRoot,
                                                  entry,
                                                  pending,
                                                  files,
                                                  uniqueFiles,
                                                  results,
                                                  ref entryCount,
                                                  ref supportedCount,
                                                  ref supportedBytes);
                if (result != null)
                    break;
            }
        }
        catch(Exception ex) when (IsExpectedEnumerationFailure(ex))
        {
            AddDirectoryFailure(results,
                                canonicalRoot,
                                directory,
                                EnumerationFailure(ex));
        }

        return result;
    }

    private DirectoryScanEntryResult? DiscoverEnumerationEntry(
        DirectoryScanRequest request,
        string canonicalRoot,
        DirectoryEntrySnapshot entry,
        Stack<DirectoryEntrySnapshot> pending,
        List<DiscoveredFile> files,
        HashSet<string> uniqueFiles,
        List<DirectoryScanEntryResult> results,
        ref long entryCount,
        ref long supportedCount,
        ref long supportedBytes)
    {
        entryCount++;
        DirectoryScanEntryResult? result = entryCount > request.MaxEntryCount
            ? DiscoveryLimitFailure(DirectoryScanReasonCodes.EntryCountLimitExceeded)
            : null;
        if (result == null)
        {
            int previousFileCount = files.Count;
            DiscoverEntry(request, canonicalRoot, entry, pending, files, results);
            if (files.Count > previousFileCount)
            {
                DiscoveredFile file = files[^1];
                result = ApplyDiscoveredFileLimits(request,
                                                   file,
                                                   uniqueFiles,
                                                   ref supportedCount,
                                                   ref supportedBytes);
            }
        }

        return result;
    }

    private static DirectoryScanEntryResult? ApplyDiscoveredFileLimits(
        DirectoryScanRequest request,
        DiscoveredFile file,
        HashSet<string> uniqueFiles,
        ref long supportedCount,
        ref long supportedBytes)
    {
        DirectoryScanEntryResult? result = null;
        bool isNewSupported = uniqueFiles.Add(file.NormalizedRelativePath)
                              && IsSupported(request, file.CanonicalPath);
        if (isNewSupported)
        {
            supportedCount++;
            bool countExceeded = supportedCount > request.MaxDocumentCount;
            bool bytesExceeded = !countExceeded
                                 && file.Snapshot.ByteLength > request.MaxTotalBytes - supportedBytes;
            result = (countExceeded, bytesExceeded) switch
                {
                    (true, _) => DiscoveryLimitFailure(DirectoryScanReasonCodes.DocumentCountLimitExceeded),
                    (_, true) => DiscoveryLimitFailure(DirectoryScanReasonCodes.TotalBytesLimitExceeded),
                    _ => null
                };
            if (result == null)
                supportedBytes += file.Snapshot.ByteLength;
        }

        return result;
    }

    private void DiscoverEntry(DirectoryScanRequest request,
                               string canonicalRoot,
                               DirectoryEntrySnapshot entry,
                               Stack<DirectoryEntrySnapshot> pending,
                               List<DiscoveredFile> files,
                               List<DirectoryScanEntryResult> results)
    {
        string? canonicalPath = TryCanonicalPath(entry.FullPath);
        if (canonicalPath == null
            || !mPathIdentity.IsContained(canonicalRoot, canonicalPath)
            || (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
                && !SnapshotPathMatches(canonicalRoot, canonicalPath, entry)))
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
                                        Stack<DirectoryEntrySnapshot> pending,
                                        List<DiscoveredFile> files,
                                        List<DirectoryScanEntryResult> results)
    {
        string displayRelativePath = DisplayRelativePath(canonicalRoot, canonicalPath);
        string normalizedRelativePath = mPathIdentity.NormalizeRelativePath(displayRelativePath);
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
                    pending.Push(entry);
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
                                                              DirectoryScanBudget budget,
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
                                                                                    ct,
                                                                                    file.Snapshot);
                result = read.Succeeded
                    ? await ProcessStableReadAsync(sink, budget, canonicalRoot, file, mediaType, read, ct)
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
                                                                    DirectoryScanBudget budget,
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
            result = budget.TryReserveBytes(read.Content.Length)
                ? await ProcessBudgetedStableReadAsync(sink, budget, file, mediaType, read, ct)
                : AggregateLimitFailure(file, DirectoryScanReasonCodes.TotalBytesLimitExceeded);

        return result;
    }

    private async Task<FileProcessingResult> ProcessBudgetedStableReadAsync(
        IDirectoryScanSink sink,
        DirectoryScanBudget budget,
        DiscoveredFile file,
        string mediaType,
        StableFileReadResult read,
        CancellationToken ct)
    {
        DirectoryEntrySnapshot before = read.Before
            ?? throw new InvalidDataException("A successful stable read is missing its initial metadata.");
        var fingerprintProvider = mDocumentIntake as IDocumentIntakeFingerprintProvider;
        DocumentExtractionFingerprint? fingerprint = fingerprintProvider?.GetFingerprint(file.DisplayRelativePath,
                                                                                            mediaType);
        var stable = new DirectoryStableDocument(file.NormalizedRelativePath,
                                                 file.DisplayRelativePath,
                                                 mediaType,
                                                 before,
                                                 read.Content)
                         {
                             ExtractionFingerprint = fingerprint
                         };
        PreparedDirectoryDocumentReuse? prepared = TryPrepareReuse(sink, stable);
        FileProcessingResult result;
        if (prepared != null)
        {
            if (budget.TryReserveSections(prepared.SectionCount))
            {
                await ((IDirectoryScanReuseSink)sink).AcceptPreparedUnchangedAsync(prepared, ct);
                result = Extracted(file, prepared.SectionCount);
            }
            else
                result = AggregateLimitFailure(file, DirectoryScanReasonCodes.SectionCountLimitExceeded);
        }
        else
            result = await IntakeAsync(sink, budget, stable, file, ct);
        return result;
    }

    private static PreparedDirectoryDocumentReuse? TryPrepareReuse(IDirectoryScanSink sink,
                                                                   DirectoryStableDocument stable) =>
        sink is IDirectoryScanReuseSink reusable
            ? reusable.TryPrepareUnchanged(stable)
            : null;

    private async Task<FileProcessingResult> IntakeAsync(IDirectoryScanSink sink,
                                                         DirectoryScanBudget budget,
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
            if (!budget.TryReserveSections(intake.Sections.Count))
                result = AggregateLimitFailure(file, DirectoryScanReasonCodes.SectionCountLimitExceeded);
            else
            {
                await sink.AcceptAsync(new DirectoryAcquiredDocument(stable, intake), ct);
                result = Extracted(file, intake.Sections.Count);
            }
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

    private static FileProcessingResult AggregateLimitFailure(DiscoveredFile file, string reasonCode) =>
        FileProcessingResult.Skipped(EntryResult(file.NormalizedRelativePath,
                                                 DirectoryScanEntryKind.File,
                                                 DirectoryScanEntryStatus.Failed,
                                                 reasonCode,
                                                 file.Snapshot.ByteLength));

    private static DirectoryScanEntryResult? AggregateDiscoveryFailure(
        DirectoryScanRequest request,
        IReadOnlyList<DiscoveredFile> supported)
    {
        DirectoryScanEntryResult? result = null;
        if (supported.Count > request.MaxDocumentCount)
        {
            result = EntryResult(string.Empty,
                                 DirectoryScanEntryKind.Root,
                                 DirectoryScanEntryStatus.Failed,
                                 DirectoryScanReasonCodes.DocumentCountLimitExceeded,
                                 0);
        }
        else
        {
            long totalBytes = 0;
            bool exceeds = false;
            foreach(DiscoveredFile file in supported)
            {
                if (file.Snapshot.ByteLength > request.MaxTotalBytes - totalBytes)
                    exceeds = true;
                else
                    totalBytes += file.Snapshot.ByteLength;
            }

            if (exceeds)
            {
                result = EntryResult(string.Empty,
                                     DirectoryScanEntryKind.Root,
                                     DirectoryScanEntryStatus.Failed,
                                     DirectoryScanReasonCodes.TotalBytesLimitExceeded,
                                     totalBytes);
            }
        }

        return result;
    }

    private static DirectoryScanEntryResult DiscoveryLimitFailure(string reasonCode) =>
        EntryResult(string.Empty,
                    DirectoryScanEntryKind.Root,
                    DirectoryScanEntryStatus.Failed,
                    reasonCode,
                    0);

    private static bool IsExpectedEnumerationFailure(Exception error) =>
        error is UnauthorizedAccessException
            or System.Security.SecurityException
            or FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or PlatformNotSupportedException;

    private static DirectoryEnumerationResult EnumerationFailure(Exception error)
    {
        string reasonCode = error switch
                                {
                                    UnauthorizedAccessException or System.Security.SecurityException =>
                                        DirectoryScanReasonCodes.DirectoryAccessDenied,
                                    FileNotFoundException or DirectoryNotFoundException =>
                                        DirectoryScanReasonCodes.DirectoryDisappeared,
                                    _ => DirectoryScanReasonCodes.DirectoryIoError
                                };
        return new DirectoryEnumerationResult([], reasonCode, error);
    }

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
                DirectoryScanReasonCodes.PathIdentityCollision => PathIdentityCollisionDetail,
                DirectoryScanReasonCodes.DirectoryCountLimitExceeded => DirectoryCountLimitExceededDetail,
                DirectoryScanReasonCodes.EntryCountLimitExceeded => EntryCountLimitExceededDetail,
                DirectoryScanReasonCodes.DocumentCountLimitExceeded => DocumentCountLimitExceededDetail,
                DirectoryScanReasonCodes.TotalBytesLimitExceeded => TotalBytesLimitExceededDetail,
                DirectoryScanReasonCodes.SectionCountLimitExceeded => SectionCountLimitExceededDetail,
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
                DirectoryScanReasonCodes.FileChangedDuringScan => reasonCode,
                DirectoryScanReasonCodes.FileTooLarge => reasonCode,
                DirectoryScanReasonCodes.FileReparsePointSkipped => reasonCode,
                _ => DirectoryScanReasonCodes.FileIoError
            };

    private static string NormalizeDirectoryReason(string reasonCode) =>
        reasonCode switch
            {
                DirectoryScanReasonCodes.DirectoryAccessDenied => reasonCode,
                DirectoryScanReasonCodes.DirectoryDisappeared => reasonCode,
                DirectoryScanReasonCodes.DirectoryReparsePointSkipped => reasonCode,
                _ => DirectoryScanReasonCodes.DirectoryIoError
            };

    private DirectoryScanEntryResult? StableReadTerminalResult(string canonicalRoot,
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
        && before.LastWriteTimeUtc == after.LastWriteTimeUtc
        && SnapshotIdentitiesAreStable(discovered, before, after);

    private static bool SnapshotIdentitiesAreStable(DirectoryEntrySnapshot discovered,
                                                    DirectoryEntrySnapshot before,
                                                    DirectoryEntrySnapshot after)
    {
        bool allUnspecified = !discovered.Identity.HasValue
                              && !before.Identity.HasValue
                              && !after.Identity.HasValue;
        bool allEqual = discovered.Identity.HasValue
                        && before.Identity.HasValue
                        && after.Identity.HasValue
                        && discovered.Identity.Value == before.Identity.Value
                        && before.Identity.Value == after.Identity.Value;
        return allUnspecified || allEqual;
    }

    private bool SnapshotPathsAreContained(string canonicalRoot,
                                           string expectedPath,
                                           DirectoryEntrySnapshot before,
                                           DirectoryEntrySnapshot after)
    {
        bool result;
        try
        {
            string beforePath = Path.GetFullPath(before.IdentityPath);
            string afterPath = Path.GetFullPath(after.IdentityPath);
            result = mPathIdentity.IsContained(canonicalRoot, beforePath)
                     && mPathIdentity.IsContained(canonicalRoot, afterPath)
                     && beforePath.Equals(expectedPath, mPathIdentity.Comparison)
                     && afterPath.Equals(expectedPath, mPathIdentity.Comparison);
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

    private bool SnapshotPathMatches(string canonicalRoot,
                                     string expectedPath,
                                     DirectoryEntrySnapshot snapshot)
    {
        bool result;
        try
        {
            string identityPath = Path.GetFullPath(snapshot.IdentityPath);
            result = mPathIdentity.IsContained(canonicalRoot, identityPath)
                     && identityPath.Equals(expectedPath, mPathIdentity.Comparison);
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

    private bool IsExcluded(DirectoryScanRequest request, string normalizedRelativePath) =>
        request.ExclusionPatterns.Any(pattern =>
                                            System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                                                pattern.Replace('\\', '/'),
                                                normalizedRelativePath,
                                                ignoreCase: !mPathIdentity.IsCaseSensitive));

    private static string NormalizeExtension(string extension)
    {
        string trimmed = extension.Trim();
        return trimmed.StartsWith('.') ? trimmed : $".{trimmed}";
    }

    private static DirectoryScanEntryKind EntryKind(DirectoryEntrySnapshot entry) =>
        entry.Attributes.HasFlag(FileAttributes.Directory)
            ? DirectoryScanEntryKind.Directory
            : DirectoryScanEntryKind.File;

    private string NormalizeRelativePath(string canonicalRoot, string fullPath) =>
        mPathIdentity.NormalizeRelativePath(DisplayRelativePath(canonicalRoot, fullPath));

    private static string DisplayRelativePath(string canonicalRoot, string fullPath)
    {
        string relativePath = Path.GetRelativePath(canonicalRoot, fullPath)
                                  .Replace('\\', '/');
        return relativePath.Equals(".", StringComparison.Ordinal) ? string.Empty : relativePath;
    }

    private string SafeOutsideRelativePath(string fullPath)
    {
        string fileName = Path.GetFileName(fullPath);
        string result = string.IsNullOrWhiteSpace(fileName) ? OutsideRootFallbackName : fileName;
        return mPathIdentity.NormalizeRelativePath(result);
    }

    private IReadOnlyList<DiscoveredFile> RemoveIdentityCollisions(
        IEnumerable<DiscoveredFile> files,
        List<DirectoryScanEntryResult> results)
    {
        var unique = new Dictionary<string, DiscoveredFile>(mPathIdentity.Comparer);
        foreach(DiscoveredFile file in files.OrderBy(item => item.NormalizedRelativePath,
                                                      mPathIdentity.Comparer)
                                            .ThenBy(item => item.DisplayRelativePath,
                                                    StringComparer.Ordinal))
        {
            if (!unique.TryAdd(file.NormalizedRelativePath, file))
            {
                results.Add(EntryResult(file.NormalizedRelativePath,
                                        DirectoryScanEntryKind.File,
                                        DirectoryScanEntryStatus.Failed,
                                        DirectoryScanReasonCodes.PathIdentityCollision,
                                        file.Snapshot.ByteLength));
            }
        }

        IReadOnlyList<DiscoveredFile> result = unique.Values.ToList();
        return result;
    }

    private static void ValidateRequest(DirectoryScanRequest request)
    {
        ArgumentException.ThrowIfNullOrEmpty(request.LibraryId);
        ArgumentException.ThrowIfNullOrEmpty(request.ScanRunId);
        ArgumentNullException.ThrowIfNull(request.RootPath);
        if (request.MaxFileBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "The file-size bound must be positive.");
        if (request.MaxDocumentCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "The document-count bound must be positive.");
        if (request.MaxDirectoryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "The directory-count bound must be positive.");
        if (request.MaxEntryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "The entry-count bound must be positive.");
        if (request.MaxTotalBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "The aggregate byte bound must be positive.");
        if (request.MaxSectionCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "The section-count bound must be positive.");
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

    private sealed record DirectoryDiscoveryResult(IReadOnlyList<DiscoveredFile> Files,
                                                   DirectoryScanEntryResult? LimitFailure);

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
    private const string PathIdentityCollisionDetail =
        "Two files resolve to the same filesystem path identity for this platform.";
    private const string DirectoryCountLimitExceededDetail =
        "The directory tree exceeds the configured directory-count limit.";
    private const string EntryCountLimitExceededDetail =
        "The directory tree exceeds the configured filesystem-entry limit.";
    private const string DocumentCountLimitExceededDetail =
        "The directory contains more supported documents than the configured library limit.";
    private const string TotalBytesLimitExceededDetail =
        "The supported documents exceed the configured aggregate byte limit.";
    private const string SectionCountLimitExceededDetail =
        "The extracted documents exceed the configured aggregate section limit.";
    private const string FileSystemIoErrorDetail = "A filesystem input/output error prevented the entry from being scanned.";
    private const string OutsideRootFallbackName = "outside-root";
}
