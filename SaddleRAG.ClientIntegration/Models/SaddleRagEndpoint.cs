// SaddleRagEndpoint.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.ClientIntegration.Models;

public sealed record SaddleRagEndpoint(
    string Url,
    int TimeoutSeconds,
    IReadOnlyList<string> ReadOnlyToolPermissions)
{
    private const string DefaultUrl = "http://localhost:6100/mcp";
    private const int DefaultTimeoutSeconds = 300;

    public static SaddleRagEndpoint Default { get; } = new(
        DefaultUrl,
        DefaultTimeoutSeconds,
        [
            McpToolNames.SearchDocs,
            McpToolNames.GetClassReference,
            McpToolNames.GetLibraryOverview,
            McpToolNames.GetLibraryHealth,
            McpToolNames.GetDashboardIndex,
            McpToolNames.GetServerLogs,
            McpToolNames.GetVersionChanges,
            McpToolNames.GetJobStatus,
            McpToolNames.GetScrapeStatus,
            McpToolNames.GetReextractStatus,
            McpToolNames.GetReembedStatus,
            McpToolNames.ListLibraries,
            McpToolNames.ListPages,
            McpToolNames.ListSymbols,
            McpToolNames.ListExcludedSymbols,
            McpToolNames.ListJobs,
            McpToolNames.ListScrapeJobs,
            McpToolNames.ListReextractJobs,
            McpToolNames.ListReembedJobs,
            McpToolNames.ListProfiles,
            McpToolNames.InspectScrape,
            McpToolNames.ReconLibrary
        ]
    );
}
