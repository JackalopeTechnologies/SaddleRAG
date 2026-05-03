// MonitorApiEndpoints.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings
using SaddleRAG.Ingestion;
using SaddleRAG.Mcp.Auth;
#endregion

namespace SaddleRAG.Mcp.Api;

public static class MonitorApiEndpoints
{
    private const string CancelJobPath      = "/jobs/{jobId}/cancel";
    private const string RescrapeLibraryPath = "/libraries/{libraryId}/rescrape";
    private const string RescrubLibraryPath  = "/libraries/{libraryId}/rescrub";
    private const string ApiGroupPath        = "/api/monitor";

    private const string CancelRequestedStatus = "CancelRequested";

    /// <summary>
    ///     Maps all /api/monitor write endpoints onto the app.
    /// </summary>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup(ApiGroupPath)
                       .RequireAuthorization(DiagnosticsWriteRequirement.PolicyName);

        group.MapPost(CancelJobPath,      CancelJob);
        group.MapPost(RescrapeLibraryPath, RescrapeLibrary);
        group.MapPost(RescrubLibraryPath,  RescrubLibrary);
    }

    private static async Task<IResult> CancelJob(string jobId, ScrapeJobRunner runner)
    {
        await runner.CancelAsync(jobId);
        return Results.Ok(new { JobId = jobId, Status = CancelRequestedStatus });
    }

    private static IResult RescrapeLibrary(string _)
    {
        return Results.StatusCode(StatusCodes.Status501NotImplemented);
    }

    private static IResult RescrubLibrary(string _)
    {
        return Results.StatusCode(StatusCodes.Status501NotImplemented);
    }
}
