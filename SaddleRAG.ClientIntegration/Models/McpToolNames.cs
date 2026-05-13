// McpToolNames.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.ClientIntegration.Models;

/// <summary>
///     The on-the-wire identifiers for the SaddleRAG MCP server's tools.
///     Used by <see cref="SaddleRagEndpoint" /> to enumerate the read-only
///     tools that clients should auto-allow without permission prompts.
///     Names match the <c>[McpServerTool(Name = "...")]</c> declarations on
///     the corresponding tool methods in <c>SaddleRAG.Mcp.Tools</c>.
/// </summary>
internal static class McpToolNames
{
    public const string SearchDocs = "mcp__saddlerag__search_docs";
    public const string GetClassReference = "mcp__saddlerag__get_class_reference";
    public const string GetLibraryOverview = "mcp__saddlerag__get_library_overview";
    public const string GetLibraryHealth = "mcp__saddlerag__get_library_health";
    public const string GetDashboardIndex = "mcp__saddlerag__get_dashboard_index";
    public const string GetServerLogs = "mcp__saddlerag__get_server_logs";
    public const string GetVersionChanges = "mcp__saddlerag__get_version_changes";
    public const string GetJobStatus = "mcp__saddlerag__get_job_status";
    public const string GetScrapeStatus = "mcp__saddlerag__get_scrape_status";
    public const string GetReextractStatus = "mcp__saddlerag__get_reextract_status";
    public const string GetReembedStatus = "mcp__saddlerag__get_reembed_status";
    public const string ListLibraries = "mcp__saddlerag__list_libraries";
    public const string ListPages = "mcp__saddlerag__list_pages";
    public const string ListSymbols = "mcp__saddlerag__list_symbols";
    public const string ListExcludedSymbols = "mcp__saddlerag__list_excluded_symbols";
    public const string ListJobs = "mcp__saddlerag__list_jobs";
    public const string ListScrapeJobs = "mcp__saddlerag__list_scrape_jobs";
    public const string ListReextractJobs = "mcp__saddlerag__list_reextract_jobs";
    public const string ListReembedJobs = "mcp__saddlerag__list_reembed_jobs";
    public const string ListProfiles = "mcp__saddlerag__list_profiles";
    public const string InspectScrape = "mcp__saddlerag__inspect_scrape";
    public const string ReconLibrary = "mcp__saddlerag__recon_library";
}
