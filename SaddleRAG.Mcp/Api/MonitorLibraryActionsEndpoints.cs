// MonitorLibraryActionsEndpoints.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion;
using SaddleRAG.Mcp.Auth;

#endregion

namespace SaddleRAG.Mcp.Api;

/// <summary>
///     Write endpoints behind <see cref="DiagnosticsWriteRequirement" /> for the
///     library hero action buttons on /monitor/libraries/{id}: Rescrape, Rescrub,
///     Delete-version.
/// </summary>
/// <remarks>
///     TODO: integration tests via WebApplicationFactory&lt;Program&gt; covering
///     401 (auth required), 200 (success), 404 (library missing), and the
///     DELETE response shape. See punch-list entry "Task 7.1 endpoint integration tests".
/// </remarks>
public static class MonitorLibraryActionsEndpoints
{
    /// <summary>
    ///     Maps the library action endpoints onto the app under
    ///     <c>/api/monitor/libraries</c>. All three sit behind
    ///     <see cref="DiagnosticsWriteRequirement.PolicyName" />.
    /// </summary>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup(LibraryActionsRoute)
                       .RequireAuthorization(DiagnosticsWriteRequirement.PolicyName);

        group.MapPost("/{libraryId}/rescrape", RescrapeAsync);
        group.MapPost("/{libraryId}/rescrub", RescrubAsync);
        group.MapDelete("/{libraryId}/versions/{version}", DeleteVersionAsync);
    }

    private static async Task<IResult> RescrapeAsync(string libraryId,
                                                     RescrapeRequest req,
                                                     IScrapeJobRepository jobs,
                                                     IScrapeJobQueue queue,
                                                     CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentNullException.ThrowIfNull(req);
        ArgumentException.ThrowIfNullOrEmpty(req.Version);
        var recent = await jobs.ListRecentAsync(RescrapeJobScanLimit, ct);
        var previous = recent.Where(r => string.Equals(r.Job.LibraryId,
                                                       libraryId,
                                                       StringComparison.OrdinalIgnoreCase)
                                      && string.Equals(r.Job.Version,
                                                       req.Version,
                                                       StringComparison.OrdinalIgnoreCase)
                                    )
                             .OrderByDescending(r => r.CreatedAt)
                             .FirstOrDefault();
        IResult result;
        if (previous is null)
        {
            result = Results.NotFound(new { Error = NoPriorScrapeError });
        }
        else
        {
            var jobId = await queue.QueueAsync(previous.Job, profile: null, ct);
            result = Results.Ok(new { JobId = jobId });
        }
        return result;
    }

    private static async Task<IResult> RescrubAsync(string libraryId,
                                                    RescrubRequest req,
                                                    RescrubJobRunner runner,
                                                    ILibraryRepository libs,
                                                    CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentNullException.ThrowIfNull(req);
        ArgumentException.ThrowIfNullOrEmpty(req.Version);
        var lib = await libs.GetLibraryAsync(libraryId, ct);
        IResult result;
        if (lib is null)
        {
            result = Results.NotFound(new { Error = UnknownLibraryError });
        }
        else
        {
            var jobId = await runner.QueueAsync(libraryId,
                                                req.Version,
                                                new RescrubOptions(),
                                                profile: null,
                                                ct
                                               );
            result = Results.Ok(new { JobId = jobId });
        }
        return result;
    }

    private static async Task<IResult> DeleteVersionAsync(string libraryId,
                                                          string version,
                                                          ILibraryRepository libs,
                                                          CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        var deleteResult = await libs.DeleteVersionAsync(libraryId, version, ct);
        return Results.Ok(new
                              {
                                  VersionsDeleted = deleteResult.VersionsDeleted,
                                  LibraryRowDeleted = deleteResult.LibraryRowDeleted,
                                  CurrentVersionRepointedTo = deleteResult.CurrentVersionRepointedTo
                              }
                         );
    }

    private const string LibraryActionsRoute = "/api/monitor/libraries";
    private const string NoPriorScrapeError = "No prior scrape job for this library/version.";
    private const string UnknownLibraryError = "Unknown library.";
    private const int RescrapeJobScanLimit = 100;
}
