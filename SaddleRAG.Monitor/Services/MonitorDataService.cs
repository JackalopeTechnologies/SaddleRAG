// MonitorDataService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Monitor.Pages;

#endregion

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Server-side data access service for the Blazor monitor pages.
///     Wraps existing repositories so Blazor components don't take
///     direct repository dependencies.
/// </summary>
public sealed class MonitorDataService
{
    /// <summary>
    ///     Initializes a new instance of <see cref="MonitorDataService" />.
    /// </summary>
    public MonitorDataService(ILibraryRepository libraries,
                              IChunkRepository chunks,
                              ILibraryProfileRepository profiles,
                              IScrapeJobRepository jobs,
                              IScrapeAuditRepository audit)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(audit);
        mLibraries = libraries;
        mChunks = chunks;
        mProfiles = profiles;
        mJobs = jobs;
        mAudit = audit;
    }

    private readonly ILibraryRepository mLibraries;
    private readonly IChunkRepository mChunks;
    private readonly ILibraryProfileRepository mProfiles;
    private readonly IScrapeJobRepository mJobs;
    private readonly IScrapeAuditRepository mAudit;

    /// <summary>
    ///     Returns a summary row for every library, including counts from the current version record.
    /// </summary>
    public async Task<IReadOnlyList<LibrarySummaryItem>> GetLibrarySummariesAsync(CancellationToken ct = default)
    {
        var libs = await mLibraries.GetAllLibrariesAsync(ct);
        var versionTasks = libs.Select(lib => lib.CurrentVersion is not null
                                                  ? mLibraries.GetVersionAsync(lib.Id, lib.CurrentVersion, ct)
                                                  : Task.FromResult<LibraryVersionRecord?>(result: null)
                                      );
        var versions = await Task.WhenAll(versionTasks);
        return libs.Zip(versions,
                        (lib, ver) => new LibrarySummaryItem
                                          {
                                              LibraryId = lib.Id,
                                              Version = lib.CurrentVersion ?? string.Empty,
                                              ChunkCount = ver?.ChunkCount ?? 0,
                                              PageCount = ver?.PageCount ?? 0,
                                              IsSuspect = ver?.Suspect ?? false,
                                              SuspectReasons = ver?.SuspectReasons ?? Array.Empty<string>(),
                                              LastScrapedAt = ver?.ScrapedAt,
                                              Hint = lib.Hint
                                          }
                       )
                   .OrderBy(s => s.LibraryId, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    /// <summary>
    ///     Returns the detail model for a single library, or <c>null</c> if not found.
    /// </summary>
    public async Task<LibraryDetailData?> GetLibraryDetailAsync(string libraryId,
                                                                CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        LibraryDetailData? result = null;
        var lib = await mLibraries.GetLibraryAsync(libraryId, ct);
        if (lib is not null)
        {
            var version = lib.CurrentVersion ?? string.Empty;
            var verRecord = string.IsNullOrEmpty(version)
                                ? null
                                : await mLibraries.GetVersionAsync(lib.Id, version, ct);

            IReadOnlyList<HostBucket> hosts = Array.Empty<HostBucket>();
            IReadOnlyDictionary<string, double> langs = new Dictionary<string, double>();
            if (!string.IsNullOrEmpty(version))
            {
                var hostMap = await mChunks.GetHostnameDistributionAsync(lib.Id, version, ct);
                hosts = hostMap.Select(kv => new HostBucket(kv.Key, kv.Value))
                               .OrderByDescending(b => b.Count)
                               .ThenBy(b => b.Host, StringComparer.OrdinalIgnoreCase)
                               .ToList();
                langs = await mChunks.GetLanguageMixAsync(lib.Id, version, ct);
            }

            result = new LibraryDetailData
                         {
                             LibraryId = lib.Id,
                             Version = version,
                             ChunkCount = verRecord?.ChunkCount ?? 0,
                             PageCount = verRecord?.PageCount ?? 0,
                             IsSuspect = verRecord?.Suspect ?? false,
                             Hint = lib.Hint,
                             SuspectReasons = verRecord?.SuspectReasons ?? Array.Empty<string>(),
                             LastScrapedAt = verRecord?.ScrapedAt,
                             LastSuspectEvaluatedAt = verRecord?.LastSuspectEvaluatedAt,
                             BoundaryIssuePct = verRecord?.BoundaryIssuePct,
                             EmbeddingProviderId = verRecord?.EmbeddingProviderId,
                             EmbeddingModelName = verRecord?.EmbeddingModelName,
                             HostnameDistribution = hosts,
                             LanguageMix = langs
                         };
        }

        return result;
    }

    /// <summary>
    ///     Returns the recon profile for a library version, or <c>null</c> when not present.
    /// </summary>
    public Task<LibraryProfile?> GetLibraryProfileAsync(string libraryId,
                                                        string version,
                                                        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        return mProfiles.GetAsync(libraryId, version, ct);
    }

    /// <summary>
    ///     Returns every indexed version for a library, sorted descending by ScrapedAt.
    /// </summary>
    public Task<IReadOnlyList<LibraryVersionRecord>> GetVersionsAsync(string libraryId,
                                                                      CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        return mLibraries.GetVersionsAsync(libraryId, ct);
    }

    /// <summary>
    ///     Returns the most-recent job id that scraped (libraryId, version), or null when no job is recorded.
    /// </summary>
    public async Task<string?> GetLatestJobIdAsync(string libraryId,
                                                   string version,
                                                   CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        var recent = await mJobs.ListRecentAsync(RecentJobsScanLimit, ct);
        var match = recent.Where(r => string.Equals(r.Job.LibraryId,
                                                    libraryId,
                                                    StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(r.Job.Version,
                                                    version,
                                                    StringComparison.OrdinalIgnoreCase))
                          .OrderByDescending(r => r.CreatedAt)
                          .FirstOrDefault();
        return match?.Id;
    }

    /// <summary>
    ///     Returns the minimal job header info (library/version/status/timestamps), or null when missing.
    /// </summary>
    public async Task<JobInfo?> GetJobInfoAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        JobInfo? result = null;
        var rec = await mJobs.GetAsync(jobId, ct);
        if (rec is not null)
        {
            result = new JobInfo
                         {
                             JobId = rec.Id,
                             LibraryId = rec.Job.LibraryId,
                             Version = rec.Job.Version,
                             Status = rec.Status.ToString(),
                             StartedAt = rec.StartedAt,
                             CompletedAt = rec.CompletedAt,
                             ErrorMessage = rec.ErrorMessage
                         };
        }

        return result;
    }

    /// <summary>
    ///     Returns the audit summary for a job id, or null when the underlying audit data is missing
    ///     and the repository surfaces that as an exception.
    /// </summary>
    public async Task<AuditSummary?> GetAuditSummaryAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        AuditSummary? result = null;
        try
        {
            result = await mAudit.SummarizeAsync(jobId, ct);
        }
        catch (Exception)
        {
            // Audit may not exist for this job; treat as absent.
        }

        return result;
    }

    private const int RecentJobsScanLimit = 100;
}
