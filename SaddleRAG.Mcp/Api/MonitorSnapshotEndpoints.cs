// MonitorSnapshotEndpoints.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Interfaces;

#endregion

namespace SaddleRAG.Mcp.Api;

public static class MonitorSnapshotEndpoints
{
    /// <summary>
    ///     Maps the read-only /api/monitor snapshot endpoints onto the app.
    /// </summary>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet(SnapshotPath, GetSnapshot);
    }

    private static IResult GetSnapshot(string jobId, IMonitorBroadcaster broadcaster)
    {
        var snapshot = broadcaster.GetJobSnapshot(jobId);
        var result = snapshot is not null ? Results.Ok(snapshot) : Results.NotFound();
        return result;
    }

    private const string SnapshotPath = "/api/monitor/jobs/{jobId}/snapshot";
}
