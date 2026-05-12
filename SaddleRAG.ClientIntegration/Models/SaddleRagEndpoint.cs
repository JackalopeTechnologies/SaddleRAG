// SaddleRagEndpoint.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.ClientIntegration.Models;

public sealed record SaddleRagEndpoint(
    string Url,
    int TimeoutSeconds,
    IReadOnlyList<string> ReadOnlyToolPermissions)
{
    private const string DefaultUrl = "http://localhost:6100/mcp";
    private const int DefaultTimeoutSeconds = 300;

    public static SaddleRagEndpoint Default { get; } = new(
        Url: DefaultUrl,
        TimeoutSeconds: DefaultTimeoutSeconds,
        ReadOnlyToolPermissions: new[]
        {
            "mcp__saddlerag__search_docs",
            "mcp__saddlerag__get_class_reference",
            "mcp__saddlerag__get_library_overview",
            "mcp__saddlerag__get_library_health",
            "mcp__saddlerag__get_dashboard_index",
            "mcp__saddlerag__get_server_logs",
            "mcp__saddlerag__get_version_changes",
            "mcp__saddlerag__get_job_status",
            "mcp__saddlerag__get_scrape_status",
            "mcp__saddlerag__get_reextract_status",
            "mcp__saddlerag__list_libraries",
            "mcp__saddlerag__list_pages",
            "mcp__saddlerag__list_symbols",
            "mcp__saddlerag__list_excluded_symbols",
            "mcp__saddlerag__list_jobs",
            "mcp__saddlerag__list_scrape_jobs",
            "mcp__saddlerag__list_reextract_jobs",
            "mcp__saddlerag__list_profiles",
            "mcp__saddlerag__inspect_scrape",
            "mcp__saddlerag__recon_library"
        });
}
