// InspectScrapeTool.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tool that exposes the scrape audit log to the LLM. Summary mode
///     returns bucketed counts, a skip-reason histogram, and a host breakdown
///     for a given job. Filter mode returns matching entries when status,
///     skipReason, or host filters are applied. Url mode returns the single
///     matching entry for a given URL.
/// </summary>
[McpServerToolType]
public static class InspectScrapeTool
{
    [McpServerTool(Name = "inspect_scrape")]
    [Description("Inspect a scrape's audit log. With no filter args, returns a top-level summary " +
                 "(kept/dropped totals, by-host breakdown, by-skip-reason histogram, sample URLs). " +
                 "With filters (status, skipReason, host, url), drills into matching entries."
                )]
    public static async Task<string> InspectScrape(RepositoryFactory factory,
                                                   [Description("Scrape job id")] string jobId,
                                                   [Description("Optional status filter: Considered, Skipped, Fetched, Failed, Indexed"
                                                               )]
                                                   string? status = null,
                                                   [Description("Optional skip reason: PatternExclude, OffSiteDepth, BinaryExt, ..."
                                                               )]
                                                   string? skipReason = null,
                                                   [Description("Optional host filter")] string? host = null,
                                                   [Description("Optional URL substring filter")]
                                                   string? url = null,
                                                   [Description("Max entries to return when filters applied (default 50)"
                                                               )]
                                                   int limit = 50,
                                                   [Description("Optional database profile name")]
                                                   string? profile = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var repo = factory.GetScrapeAuditRepository(profile);

        bool hasFilter = status != null || skipReason != null || host != null || url != null;
        bool isUrlMode = !string.IsNullOrEmpty(url) && status == null && skipReason == null && host == null;
        string modeKey = (hasFilter, isUrlMode) switch
            {
                (false, var _) => ModeLabelSummary,
                (true, true) => ModeLabelUrl,
                (true, false) => ModeLabelFilter
            };

        string result = modeKey switch
            {
                ModeLabelSummary => await BuildSummaryResultAsync(repo, jobId, ct),
                ModeLabelUrl => await BuildUrlResultAsync(repo, jobId, url, ct),
                var _ => await BuildFilterResultAsync(repo,
                                                      jobId,
                                                      status,
                                                      skipReason,
                                                      host,
                                                      url,
                                                      limit,
                                                      ct
                                                     )
            };

        return result;
    }

    private static async Task<string> BuildSummaryResultAsync(IScrapeAuditRepository repo,
                                                              string jobId,
                                                              CancellationToken ct)
    {
        var summary = await repo.SummarizeAsync(jobId, ct);
        return JsonSerializer.Serialize(new
                                            {
                                                JobId = jobId,
                                                Mode = ModeLabelSummary,
                                                Summary = summary
                                            },
                                        smJsonOptions
                                       );
    }

    private static async Task<string> BuildUrlResultAsync(IScrapeAuditRepository repo,
                                                          string jobId,
                                                          string? url,
                                                          CancellationToken ct)
    {
        string urlValue = url ?? string.Empty;
        var entry = string.IsNullOrEmpty(urlValue)
                        ? null
                        : await repo.GetByUrlAsync(jobId, urlValue, ct);
        string result;
        if (entry is null)
        {
            result = JsonSerializer.Serialize(new
                                                  {
                                                      JobId = jobId,
                                                      Mode = ModeLabelUrl,
                                                      Status = StatusLabelNotFound,
                                                      Url = urlValue
                                                  },
                                              smJsonOptions
                                             );
        }
        else
        {
            result = JsonSerializer.Serialize(new
                                                  {
                                                      JobId = jobId,
                                                      Mode = ModeLabelUrl,
                                                      Status = StatusLabelFound,
                                                      Entry = entry
                                                  },
                                              smJsonOptions
                                             );
        }

        return result;
    }

    private static async Task<string> BuildFilterResultAsync(IScrapeAuditRepository repo,
                                                             string jobId,
                                                             string? status,
                                                             string? skipReason,
                                                             string? host,
                                                             string? url,
                                                             int limit,
                                                             CancellationToken ct)
    {
        var statusEnum = ParseEnum<AuditStatus>(status);
        var reasonEnum = ParseEnum<AuditSkipReason>(skipReason);
        var entries = await repo.QueryAsync(jobId,
                                            statusEnum,
                                            reasonEnum,
                                            host,
                                            url,
                                            limit,
                                            ct
                                           );
        string result;
        if (entries.Count == 0)
        {
            var summary = await repo.SummarizeAsync(jobId, ct);
            if (summary.TotalConsidered == 0)
            {
                result = JsonSerializer.Serialize(new
                                                      {
                                                          JobId = jobId,
                                                          Mode = ModeLabelFilter,
                                                          Status = StatusLabelNotFound
                                                      },
                                                  smJsonOptions
                                                 );
            }
            else
            {
                result = JsonSerializer.Serialize(new
                                                      {
                                                          JobId = jobId,
                                                          Mode = ModeLabelFilter,
                                                          AppliedFilters =
                                                              new
                                                                  {
                                                                      Status = status, SkipReason = skipReason,
                                                                      Host = host, Url = url, Limit = limit
                                                                  },
                                                          Count = 0,
                                                          Entries = entries
                                                      },
                                                  smJsonOptions
                                                 );
            }
        }
        else
        {
            result = JsonSerializer.Serialize(new
                                                  {
                                                      JobId = jobId,
                                                      Mode = ModeLabelFilter,
                                                      AppliedFilters =
                                                          new
                                                              {
                                                                  Status = status, SkipReason = skipReason, Host = host,
                                                                  Url = url, Limit = limit
                                                              },
                                                      entries.Count,
                                                      Entries = entries
                                                  },
                                              smJsonOptions
                                             );
        }

        return result;
    }

    private static T? ParseEnum<T>(string? raw)
        where T : struct, Enum
        => string.IsNullOrEmpty(raw)                          ? null :
           Enum.TryParse<T>(raw, ignoreCase: true, out var v) ? v : null;

    private const string ModeLabelSummary = "summary";
    private const string ModeLabelFilter = "filter";
    private const string ModeLabelUrl = "url";
    private const string StatusLabelFound = "found";
    private const string StatusLabelNotFound = "not_found";

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions
                                                                      {
                                                                          WriteIndented = true,
                                                                          PropertyNamingPolicy = null,
                                                                          Converters = { new JsonStringEnumConverter() }
                                                                      };
}
