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
///     Reads from the unified <c>jobs</c> collection, projects every
///     row into a <see cref="JobRow" />, applies filters, sorts by
///     CreatedAt desc, and truncates to <c>limit</c>. Replaces the
///     three-collection union the prior implementation maintained;
///     the union work now lives entirely in the database (one
///     collection serves every job type).
/// </summary>
public sealed class UnifiedJobView : IUnifiedJobView
{
    public UnifiedJobView(IJobRepository jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        mJobs = jobs;
    }

    private readonly IJobRepository mJobs;

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobRow>> ListAsync(ScrapeJobStatus? statusFilter,
                                                       JobType? typeFilter,
                                                       string? libraryFilter,
                                                       int limit,
                                                       CancellationToken ct = default)
    {
        // Fetch 2x the requested limit so client-side library / status
        // filtering can still produce up to `limit` matches after
        // pruning; the repo's own jobType filter narrows server-side.
        IReadOnlyList<JobRecord> records = await mJobs.ListRecentAsync(typeFilter, limit * 2, ct);
        IReadOnlyList<JobRow> result = ApplyFilters(records.Select(Project), statusFilter, libraryFilter, limit);
        return result;
    }

    /// <inheritdoc />
    public async Task<JobRow?> GetAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        JobRecord? record = await mJobs.GetAsync(jobId, ct);
        JobRow? result = record is null ? null : Project(record);
        return result;
    }

    private static IReadOnlyList<JobRow> ApplyFilters(IEnumerable<JobRow> rows,
                                                       ScrapeJobStatus? statusFilter,
                                                       string? libraryFilter,
                                                       int limit) =>
        rows.Where(r => statusFilter is null || r.Status == statusFilter)
            .Where(r => string.IsNullOrEmpty(libraryFilter) ||
                        (r.LibraryId is not null &&
                         r.LibraryId.Contains(libraryFilter, StringComparison.OrdinalIgnoreCase))
                  )
            .OrderByDescending(r => r.CreatedAt)
            .ThenBy(r => r.JobId, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

    private static JobRow Project(JobRecord r)
    {
        (string? renameTo, string? scanPath) = ParseDisplayHints(r);
        return new JobRow
                   {
                       JobId          = r.Id,
                       Type           = r.JobType,
                       Status         = (ScrapeJobStatus) (int) r.Status,
                       CreatedAt      = r.CreatedAt,
                       StartedAt      = r.StartedAt,
                       CompletedAt    = r.CompletedAt,
                       LibraryId      = r.LibraryId,
                       Version        = r.Version,
                       RenameToId     = renameTo,
                       ScanPath       = scanPath,
                       ItemsProcessed = (int) r.ItemsProcessed,
                       ItemsTotal     = (int) r.ItemsTotal,
                       ItemsLabel     = r.ItemsLabel,
                       ErrorCount     = r.ErrorCount,
                       ErrorMessage   = r.ErrorMessage
                   };
    }

    private static (string? RenameTo, string? ScanPath) ParseDisplayHints(JobRecord r)
    {
        (string? RenameTo, string? ScanPath) result = (null, null);
        if (!string.IsNullOrEmpty(r.InputJson) &&
            r.JobType is JobType.RenameLibrary or JobType.IndexProjectDependencies)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(r.InputJson);
                result = r.JobType switch
                         {
                             JobType.RenameLibrary             => (ReadProperty(doc, NewIdJsonProperty), null),
                             JobType.IndexProjectDependencies  => (null, ReadProperty(doc, PathJsonProperty)),
                             _                                 => (null, null)
                         };
            }
            catch(JsonException)
            {
                // Malformed input json — leave both null.
            }
        }
        return result;
    }

    private static string? ReadProperty(JsonDocument doc, string propertyName) =>
        doc.RootElement.TryGetProperty(propertyName, out JsonElement el) ? el.GetString() : null;

    private const string NewIdJsonProperty = "newId";
    private const string PathJsonProperty = "path";
}
