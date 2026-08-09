// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Ingestion.Documents.Intake;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Runs one explicitly requested, non-publishing directory preview.</summary>
public sealed class DirectoryScanner : IDirectoryScanner
{
    public DirectoryScanner(IDirectoryScanFileSystem fileSystem,
                            IDocumentIntake documentIntake,
                            ISourceDocumentRepository sourceDocuments,
                            ILogger<DirectoryScanner> logger,
                            TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(documentIntake);
        ArgumentNullException.ThrowIfNull(sourceDocuments);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        mEngine = new DirectoryScanEngine(fileSystem,
                                          documentIntake,
                                          logger,
                                          timeProvider,
                                          DirectoryPathIdentity.Platform,
                                          sharedLoggerCategory: true);
        mSourceDocuments = sourceDocuments;
        mLogger = logger;
        mTimeProvider = timeProvider;
    }

    private readonly DirectoryScanEngine mEngine;
    private readonly ILogger<DirectoryScanner> mLogger;
    private readonly ISourceDocumentRepository mSourceDocuments;
    private readonly TimeProvider mTimeProvider;

    public async Task<DirectoryScanReport> ScanAsync(DirectoryScanRequest request,
                                                     CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string workspaceLibraryId = MakeWorkspaceLibraryId(request.LibraryId, request.ScanRunId);
        DateTime startedAtUtc = mTimeProvider.GetUtcNow().UtcDateTime;
        var sink = new DirectoryPreviewSink(mSourceDocuments,
                                            workspaceLibraryId,
                                            request.ScanRunId,
                                            mTimeProvider);
        DirectoryScanReport? report = null;
        OperationCanceledException? cancellation = null;
        try
        {
            report = await mEngine.ScanAsync(request, sink, onProgress: null, cancellationToken);
        }
        catch(OperationCanceledException ex)
        {
            cancellation = ex;
        }
        catch(Exception ex)
        {
            mLogger.LogError(ex, "The explicit directory preview failed.");
            report = FailedReport(request, startedAtUtc, ScanFailedDetail);
        }
        finally
        {
            try
            {
                await mSourceDocuments.DeleteCandidateScanRunAsync(workspaceLibraryId,
                                                                   request.ScanRunId,
                                                                   CancellationToken.None);
            }
            catch(Exception cleanupError)
            {
                mLogger.LogError(cleanupError, "The directory-preview candidate cleanup failed.");
                if (cancellation != null)
                    cancellation.Data[CleanupFailureDataKey] = cleanupError;
                else
                    report = FailedReport(request, startedAtUtc, CleanupFailedDetail);
            }
        }

        if (cancellation != null)
            ExceptionDispatchInfo.Capture(cancellation).Throw();
        return report ?? FailedReport(request, startedAtUtc, ScanFailedDetail);
    }

    private DirectoryScanReport FailedReport(DirectoryScanRequest request,
                                             DateTime startedAtUtc,
                                             string detail) =>
        new()
            {
                LibraryId = request.LibraryId,
                ScanRunId = request.ScanRunId,
                Status = DirectoryScanStatus.Failed,
                ReasonCode = DirectoryScanReasonCodes.ScanFailed,
                Detail = detail,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = mTimeProvider.GetUtcNow().UtcDateTime,
                Entries = [],
                DiscoveredCount = 0,
                ExtractedCount = 0,
                SkippedCount = 0,
                FailedCount = 0
            };

    private static string MakeWorkspaceLibraryId(string libraryId, string scanRunId) =>
        $"preview/{libraryId}/{scanRunId}";

    private const string CleanupFailureDataKey = "DirectoryPreviewCleanupFailure";
    private const string ScanFailedDetail = "The directory preview failed.";
    private const string CleanupFailedDetail = "The directory preview cleanup failed.";
}
