// MonitorDataService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
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
    public MonitorDataService(ILibraryRepository libraries, IChunkRepository chunks)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(chunks);
        mLibraries = libraries;
        mChunks = chunks;
    }

    private readonly ILibraryRepository mLibraries;
    private readonly IChunkRepository mChunks;

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
}
