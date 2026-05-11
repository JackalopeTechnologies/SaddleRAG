// SettingsTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;
using Serilog.Core;
using Serilog.Events;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tools for runtime configuration changes.
///     Enables the LLM to flip features like the reranker strategy
///     and log level without restarting the service.
/// </summary>
[McpServerToolType]
public static class SettingsTools
{
    [McpServerTool(Name = "set_rerank_strategy")]
    [Description("Set the active reranker strategy at runtime. " +
                 "'Off' = no reranking (hybrid score is final, fastest; current default). " +
                 "'Llm' and 'CrossEncoder' are currently non-functional stubs: the server accepts the " +
                 "value, but requests behave like NoOp reranking until those pipelines are calibrated. " +
                 "Use 'Off' for predictable production behavior. " +
                 "Omit strategy to read current state."
                )]
    public static string SetRerankStrategy(ToggleableReRanker reRanker,
                                           [Description("Reranker strategy: Off, Llm, or CrossEncoder. Omit to read current state."
                                                       )]
                                           string? strategy = null)
    {
        ArgumentNullException.ThrowIfNull(reRanker);

        string? warning = null;
        if (!string.IsNullOrEmpty(strategy))
        {
            if (Enum.TryParse<ReRankerStrategy>(strategy, ignoreCase: true, out var parsed))
                reRanker.Strategy = parsed;
            else
                warning = string.Format(InvalidStrategyWarningFormat, strategy);
        }

        var response = new
                           {
                               Strategy = reRanker.Strategy.ToString(),
                               Warning = warning
                           };
        var result = JsonSerializer.Serialize(response, smJsonOptions);
        return result;
    }

    [McpServerTool(Name = "set_query_planner_strategy")]
    [Description("Set the active query-planner strategy at runtime. " +
                 "'Off' = no local planning; the caller query flows straight to retrieval. " +
                 "'Llm' = one local LLM call produces conservative search hints before retrieval. " +
                 "Omit strategy to read current state."
                )]
    public static string SetQueryPlannerStrategy(IOptions<RankingSettings> rankingOptions,
                                                 [Description("Query planner strategy: Off or Llm. Omit to read current state.")]
                                                 string? strategy = null)
    {
        ArgumentNullException.ThrowIfNull(rankingOptions);

        var rankingSettings = rankingOptions.Value;
        string? warning = null;
        if (!string.IsNullOrEmpty(strategy))
        {
            if (Enum.TryParse<QueryPlannerStrategy>(strategy, ignoreCase: true, out var parsed))
                rankingSettings.QueryPlannerStrategy = parsed;
            else
                warning = string.Format(InvalidQueryPlannerStrategyWarningFormat, strategy);
        }

        var response = new
                           {
                               Strategy = rankingSettings.QueryPlannerStrategy.ToString(),
                               Warning = warning
                           };
        var result = JsonSerializer.Serialize(response, smJsonOptions);
        return result;
    }

    [McpServerTool(Name = "toggle_logging")]
    [Description("Change the minimum log level at runtime. " +
                 "Use 'Warning' or 'Error' for quiet production operation. " +
                 "Use 'Information' or 'Debug' for troubleshooting. " +
                 "Returns the current level."
                )]
    public static string ToggleLogging(LoggingLevelSwitch levelSwitch,
                                       [Description("Minimum log level: Verbose, Debug, Information, Warning, Error, Fatal. Omit to check current level."
                                                   )]
                                       string? level = null)
    {
        ArgumentNullException.ThrowIfNull(levelSwitch);

        string? warning = null;
        if (!string.IsNullOrEmpty(level))
        {
            if (Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var parsed))
                levelSwitch.MinimumLevel = parsed;
            else
                warning = string.Format(InvalidLevelWarningFormat, level);
        }

        var response = new
                           {
                               MinimumLogLevel = levelSwitch.MinimumLevel.ToString(),
                               Warning = warning
                           };
        var result = JsonSerializer.Serialize(response, smJsonOptions);
        return result;
    }

    private const string InvalidLevelWarningFormat =
        "Unknown level '{0}'. Valid values: Verbose, Debug, Information, Warning, Error, Fatal. Current level unchanged.";

    private const string InvalidStrategyWarningFormat =
        "Unknown strategy '{0}'. Valid values: Off, Llm, CrossEncoder. Current strategy unchanged.";

    private const string InvalidQueryPlannerStrategyWarningFormat =
        "Unknown strategy '{0}'. Valid values: Off, Llm. Current query planner strategy unchanged.";

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
