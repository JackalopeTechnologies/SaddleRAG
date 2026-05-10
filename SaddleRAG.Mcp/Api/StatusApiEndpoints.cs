// StatusApiEndpoints.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Mcp.Api;

/// <summary>
///     Read-only status endpoint consumed by the VS Code extension sidebar.
///     No authentication required — same policy as <c>/health</c>.
/// </summary>
public static class StatusApiEndpoints
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet(StatusPath, GetStatus);
    }

    internal static async Task<StatusResponse> BuildStatusResponseAsync(
        ILibraryRepository libraryRepository,
        IScrapeJobRepository jobRepository,
        CancellationToken ct)
    {
        IReadOnlyList<LibraryRecord> libs = await libraryRepository.GetAllLibrariesAsync(ct);
        IReadOnlyList<ScrapeJobRecord> running = await jobRepository.ListRunningJobsAsync(ct);

        IReadOnlyList<LibraryStatusItem> libraryItems = libs
            .Select(l => new LibraryStatusItem(l.Name, l.CurrentVersion, HealthyStatus))
            .ToList();

        IReadOnlyList<ActiveJobItem> jobItems = running
            .Select(j => new ActiveJobItem(j.Id, j.Job.LibraryId, j.PipelineState))
            .ToList();

        return new StatusResponse(libraryItems, jobItems);
    }

    private static async Task<IResult> GetStatus(
        ILibraryRepository libraryRepository,
        IScrapeJobRepository jobRepository,
        CancellationToken ct)
    {
        StatusResponse response = await BuildStatusResponseAsync(libraryRepository, jobRepository, ct);
        return Results.Ok(response);
    }

    private const string StatusPath = "/api/status";
    private const string HealthyStatus = "Healthy";
}
