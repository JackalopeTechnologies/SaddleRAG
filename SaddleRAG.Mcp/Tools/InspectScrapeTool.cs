// InspectScrapeTool.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tool that exposes the scrape audit log to the LLM. Summary mode
///     returns bucketed counts, a skip-reason histogram, and a host breakdown
///     for a given job. Filter and single-URL drill-down modes ship in Task 11.
/// </summary>
[McpServerToolType]
public static class InspectScrapeTool
{
    [McpServerTool(Name = "inspect_scrape")]
    [Description("Inspect a scrape's audit log. With no filter args, returns a top-level summary " +
                 "(kept/dropped totals, by-host breakdown, by-skip-reason histogram, sample URLs). " +
                 "With filters (status, skipReason, host, url), drills into matching entries.")]
    public static async Task<string> InspectScrape(RepositoryFactory factory,
                                                    [Description("Scrape job id")]
                                                    string jobId,
                                                    [Description("Optional status filter: Considered, Skipped, Fetched, Failed, Indexed")]
                                                    string? status = null,
                                                    [Description("Optional skip reason: PatternExclude, OffSiteDepth, BinaryExt, ...")]
                                                    string? skipReason = null,
                                                    [Description("Optional host filter")]
                                                    string? host = null,
                                                    [Description("Optional URL substring filter")]
                                                    string? url = null,
                                                    [Description("Max entries to return when filters applied (default 50)")]
                                                    int limit = 50,
                                                    [Description("Optional database profile name")]
                                                    string? profile = null,
                                                    CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var repo = factory.GetScrapeAuditRepository(profile);

        bool hasFilter = status != null || skipReason != null || host != null || url != null;
        string result;
        if (hasFilter)
        {
            // Filter mode lands in Task 11 — placeholder for now.
            result = JsonSerializer.Serialize(new
            {
                JobId = jobId,
                Mode = ModeLabelFilter,
                Note = FilterModePlaceholderNote
            }, smJsonOptions);
        }
        else
        {
            var summary = await repo.SummarizeAsync(jobId, ct);
            result = JsonSerializer.Serialize(new
            {
                JobId = jobId,
                Mode = ModeLabelSummary,
                Summary = summary
            }, smJsonOptions);
        }

        return result;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    private const string ModeLabelSummary = "summary";
    private const string ModeLabelFilter = "filter";
    private const string FilterModePlaceholderNote = "Filter mode lands in Task 11";
}
