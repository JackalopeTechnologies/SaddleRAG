// MonitorApiEndpoints.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Ingestion;
using SaddleRAG.Mcp.Auth;

#endregion

namespace SaddleRAG.Mcp.Api;

public static class MonitorApiEndpoints
{
    /// <summary>
    ///     Maps all /api/monitor endpoints onto the app. Write actions live
    ///     under a group requiring <see cref="DiagnosticsWriteRequirement.PolicyName" />;
    ///     read-only endpoints are mapped directly without auth.
    /// </summary>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup(ApiGroupPath)
                       .RequireAuthorization(DiagnosticsWriteRequirement.PolicyName);

        group.MapPost(CancelJobPath, CancelJob);

        app.MapGet(ApiGroupPath + QueryMetricsPath,
                   (IQueryMetrics metrics) => Results.Ok(metrics.Snapshot())
                  );
    }

    private static async Task<IResult> CancelJob(string jobId, JobCancellationService cancellation)
    {
        await cancellation.CancelAsync(jobId);
        return Results.Ok(new { JobId = jobId, Status = CancelRequestedStatus });
    }

    private const string CancelJobPath = "/jobs/{jobId}/cancel";
    private const string QueryMetricsPath = "/query-metrics";
    private const string ApiGroupPath = "/api/monitor";

    private const string CancelRequestedStatus = "CancelRequested";
}
