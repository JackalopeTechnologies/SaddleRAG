// MonitorDoclingLaunchEndpoints.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Mcp.Api;

/// <summary>
///     Reports whether document work is waiting on a Docling that is not ready.
///     <para>
///         Read-only by design. The MCP service runs as LocalSystem and starts nothing;
///         this endpoint states a fact and the user-session tray decides what to do about
///         it. Reusing the existing localhost Monitor boundary avoids introducing another
///         IPC transport, port, or security surface just to carry one flag.
///     </para>
/// </summary>
public static class MonitorDoclingLaunchEndpoints
{
    /// <summary>Maps the read-only Docling launch-request endpoint onto the app.</summary>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet(LaunchRequestPath, GetLaunchRequestAsync);
    }

    private static async Task<IResult> GetLaunchRequestAsync(string? profile,
                                                             IDoclingLaunchRequestService launchRequests,
                                                             CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(launchRequests);
        DoclingLaunchRequestStatus status = await launchRequests.GetAsync(profile, ct);
        return Results.Ok(status);
    }

    private const string LaunchRequestPath = "/api/monitor/docling-launch-request";
}
