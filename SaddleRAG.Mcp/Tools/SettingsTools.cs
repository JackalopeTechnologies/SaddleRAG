// SettingsTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Enums;
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
                 "'Off' = no reranking; hybrid (vector || BM25) score is final. Fastest, predictable, the shipped default. " +
                 "'Onnx' = in-process cross-encoder reranker via Microsoft.ML.OnnxRuntime. " +
                 "Scores (query, doc) pairs using the model named by Onnx.ActiveRerankerModel " +
                 "(default mxbai-rerank-base-v1). Cross-encoder output is sigmoid-mapped to (0, 1) and used " +
                 "directly as the final score for the top-K candidates; the pass-through tail is appended below. " +
                 "Adds ~150 ms per search on CPU once the rerank session is warm. " +
                 "Omit strategy to read current state."
                )]
    public static string SetRerankStrategy(ToggleableReRanker reRanker,
                                           [Description("Reranker strategy: Off or Onnx. Omit to read current state."
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
        "Unknown strategy '{0}'. Valid values: Off, Onnx. Current strategy unchanged.";

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
