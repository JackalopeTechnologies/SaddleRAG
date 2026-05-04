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
    /// <summary>
    ///     Maps all /api/monitor write endpoints onto the app.
    /// </summary>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup(ApiGroupPath)
                       .RequireAuthorization(DiagnosticsWriteRequirement.PolicyName);

        group.MapPost(CancelJobPath, CancelJob);
    }

    private static async Task<IResult> CancelJob(string jobId, ScrapeJobRunner runner)
    {
        await runner.CancelAsync(jobId);
        return Results.Ok(new { JobId = jobId, Status = CancelRequestedStatus });
    }

    private const string CancelJobPath = "/jobs/{jobId}/cancel";
    private const string ApiGroupPath = "/api/monitor";

    private const string CancelRequestedStatus = "CancelRequested";
}
