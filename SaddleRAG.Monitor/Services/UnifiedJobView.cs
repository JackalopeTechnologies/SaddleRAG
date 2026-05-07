// UnifiedJobView.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.Json;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Reads three job collections in parallel, projects each into a
///     <see cref="JobRow" />, applies filters, sorts by CreatedAt desc,
///     and truncates to <c>limit</c>.
/// </summary>
public sealed class UnifiedJobView : IUnifiedJobView
{
    public UnifiedJobView(IScrapeJobRepository scrapeJobs,
                          IBackgroundJobRepository backgroundJobs,
                          IRescrubJobRepository rescrubJobs)
    {
        ArgumentNullException.ThrowIfNull(scrapeJobs);
        ArgumentNullException.ThrowIfNull(backgroundJobs);
        ArgumentNullException.ThrowIfNull(rescrubJobs);
        mScrape     = scrapeJobs;
        mBackground = backgroundJobs;
        mRescrub    = rescrubJobs;
    }

    private readonly IScrapeJobRepository mScrape;
    private readonly IBackgroundJobRepository mBackground;
    private readonly IRescrubJobRepository mRescrub;

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobRow>> ListAsync(ScrapeJobStatus? statusFilter,
                                                       JobType? typeFilter,
                                                       string? libraryFilter,
                                                       int limit,
                                                       CancellationToken ct = default)
    {
        var fetchLimit   = Math.Max(limit * 2, limit);
        var scrapeTask   = mScrape.ListRecentAsync(fetchLimit, ct);
        var backgroundTask = mBackground.ListRecentAsync(jobType: null, fetchLimit, ct);
        var rescrubTask  = mRescrub.ListRecentAsync(fetchLimit, ct);
        await Task.WhenAll(scrapeTask, backgroundTask, rescrubTask);

        IEnumerable<JobRow> rows = scrapeTask.Result.Select(ProjectScrape)
                                             .Concat(backgroundTask.Result.Select(ProjectBackground))
                                             .Concat(rescrubTask.Result.Select(ProjectRescrub));

        var filtered = rows
                      .Where(r => statusFilter is null || r.Status == statusFilter)
                      .Where(r => typeFilter is null || r.Type == typeFilter)
                      .Where(r => string.IsNullOrEmpty(libraryFilter)
                               || (r.LibraryId is not null
                                && r.LibraryId.Contains(libraryFilter, StringComparison.OrdinalIgnoreCase)))
                      .OrderByDescending(r => r.CreatedAt)
                      .ThenBy(r => r.JobId, StringComparer.Ordinal)
                      .Take(limit)
                      .ToList();
        return filtered;
    }

    /// <inheritdoc />
    public async Task<JobRow?> GetAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        JobRow? result = null;

        var scrape = await mScrape.GetAsync(jobId, ct);
        if (scrape is not null)
            result = ProjectScrape(scrape);

        if (result is null)
        {
            var bg = await mBackground.GetAsync(jobId, ct);
            if (bg is not null)
                result = ProjectBackground(bg);
        }

        if (result is null)
        {
            var rs = await mRescrub.GetAsync(jobId, ct);
            if (rs is not null)
                result = ProjectRescrub(rs);
        }

        return result;
    }

    private static JobRow ProjectScrape(ScrapeJobRecord r) => new JobRow
                                                                  {
                                                                      JobId          = r.Id,
                                                                      Type           = JobType.Scrape,
                                                                      Status         = r.Status,
                                                                      CreatedAt      = r.CreatedAt,
                                                                      StartedAt      = r.StartedAt,
                                                                      CompletedAt    = r.CompletedAt,
                                                                      LibraryId      = r.Job.LibraryId,
                                                                      Version        = r.Job.Version,
                                                                      ItemsProcessed = r.PagesCompleted,
                                                                      ItemsTotal     = 0,
                                                                      ItemsLabel     = PagesItemsLabel,
                                                                      ErrorCount     = r.ErrorCount,
                                                                      ErrorMessage   = r.ErrorMessage
                                                                  };

    private static JobRow ProjectBackground(BackgroundJobRecord r)
    {
        var (renameTo, scanPath) = ParseInputJson(r);
        return new JobRow
                   {
                       JobId          = r.Id,
                       Type           = MapBackgroundType(r.JobType),
                       Status         = r.Status,
                       CreatedAt      = r.CreatedAt,
                       StartedAt      = r.StartedAt,
                       CompletedAt    = r.CompletedAt,
                       LibraryId      = r.LibraryId,
                       Version        = r.Version,
                       RenameToId     = renameTo,
                       ScanPath       = scanPath,
                       ItemsProcessed = r.ItemsProcessed,
                       ItemsTotal     = r.ItemsTotal,
                       ItemsLabel     = r.ItemsLabel,
                       ErrorCount     = 0,
                       ErrorMessage   = r.ErrorMessage
                   };
    }

    private static JobRow ProjectRescrub(RescrubJobRecord r) => new JobRow
                                                                    {
                                                                        JobId          = r.Id,
                                                                        Type           = JobType.Rescrub,
                                                                        Status         = r.Status,
                                                                        CreatedAt      = r.CreatedAt,
                                                                        StartedAt      = r.StartedAt,
                                                                        CompletedAt    = r.CompletedAt,
                                                                        LibraryId      = r.LibraryId,
                                                                        Version        = r.Version,
                                                                        ItemsProcessed = r.ChunksProcessed,
                                                                        ItemsTotal     = r.ChunksTotal,
                                                                        ItemsLabel     = ChunksItemsLabel,
                                                                        ErrorCount     = 0,
                                                                        ErrorMessage   = r.ErrorMessage
                                                                    };

    private static JobType MapBackgroundType(string jobType)
    {
        JobType result = JobType.Rechunk;
        switch (jobType)
        {
            case BackgroundJobTypes.Rechunk:                  result = JobType.Rechunk; break;
            case BackgroundJobTypes.RenameLibrary:            result = JobType.RenameLibrary; break;
            case BackgroundJobTypes.DeleteVersion:            result = JobType.DeleteVersion; break;
            case BackgroundJobTypes.DeleteLibrary:            result = JobType.DeleteLibrary; break;
            case BackgroundJobTypes.DryRunScrape:             result = JobType.DryRunScrape; break;
            case BackgroundJobTypes.IndexProjectDependencies: result = JobType.IndexProjectDependencies; break;
            case BackgroundJobTypes.SubmitUrlCorrection:      result = JobType.SubmitUrlCorrection; break;
        }
        return result;
    }

    private const string NewIdJsonProperty   = "newId";
    private const string PathJsonProperty    = "path";
    private const string PagesItemsLabel     = "pages";
    private const string ChunksItemsLabel    = "chunks";

    private static (string? RenameTo, string? ScanPath) ParseInputJson(BackgroundJobRecord r)
    {
        string? renameTo = null;
        string? scanPath = null;
        if (!string.IsNullOrEmpty(r.InputJson)
         && (r.JobType == BackgroundJobTypes.RenameLibrary
          || r.JobType == BackgroundJobTypes.IndexProjectDependencies))
        {
            try
            {
                using var doc = JsonDocument.Parse(r.InputJson);
                if (r.JobType == BackgroundJobTypes.RenameLibrary
                 && doc.RootElement.TryGetProperty(NewIdJsonProperty, out var newIdEl))
                {
                    renameTo = newIdEl.GetString();
                }
                if (r.JobType == BackgroundJobTypes.IndexProjectDependencies
                 && doc.RootElement.TryGetProperty(PathJsonProperty, out var pathEl))
                {
                    scanPath = pathEl.GetString();
                }
            }
            catch (JsonException)
            {
                // Malformed input json — leave both null.
            }
        }
        return (renameTo, scanPath);
    }
}
