// CancellationTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Enums;
using SaddleRAG.Ingestion;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tool for cancelling any in-flight cancellable job. Scrape,
///     dry-run, rechunk, reembed, and reextract jobs cooperatively
///     observe cancellation. Atomic mutations (renames, deletes,
///     dependency indexing, URL corrections, cleanup jobs) cannot be
///     cancelled — the tool returns <c>NotCancellable</c> for those.
/// </summary>
[McpServerToolType]
public static class CancellationTools
{
    [McpServerTool(Name = "cancel_job")]
    [Description("Cancel a running job. Signals the pipeline cancellation token for active scrape, " +
                 "dryrun_scrape, rechunk, reembed, or reextract jobs; marks the DB row Cancelled " +
                 "directly for jobs orphaned by a process restart. Refuses (NotCancellable) for " +
                 "atomic mutations (renames, deletes, dependency indexing, URL corrections, cleanup " +
                 "jobs) — those must run to completion. No-op for jobs already Completed/Failed/Cancelled. " +
                 "Partial results are kept on cancel — call delete_version to clear them, or " +
                 "submit_url_correction if the cancel was triggered by a wrong URL (that tool clears " +
                 "partial data and re-queues with a corrected URL in one step)."
                )]
    public static async Task<string> CancelJob(JobCancellationService cancellation,
                                               [Description("Job id from list_scrape_jobs or get_*_status")]
                                               string jobId,
                                               [Description("Optional database profile name")]
                                               string? profile = null,
                                               CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var outcome = await cancellation.CancelAsync(jobId, profile, ct);

        var response = new
                           {
                               JobId = jobId,
                               Outcome = outcome.ToString(),
                               Message = outcome switch
                                   {
                                       CancelScrapeOutcome.Signalled => SignalledMessage,
                                       CancelScrapeOutcome.OrphanCleanedUp => OrphanCleanedUpMessage,
                                       CancelScrapeOutcome.AlreadyTerminal => AlreadyTerminalMessage,
                                       CancelScrapeOutcome.NotFound => NotFoundMessage,
                                       CancelScrapeOutcome.NotCancellable => NotCancellableMessage,
                                       var _ => UnknownOutcomeMessage
                                   }
                           };
        var json = JsonSerializer.Serialize(response, smJsonOptions);
        return json;
    }

    private const string SignalledMessage = "Pipeline cancellation signalled. Job will transition to Cancelled.";
    private const string OrphanCleanedUpMessage = "Job had no active runner; DB row marked Cancelled directly.";
    private const string AlreadyTerminalMessage = "Job is already Completed, Failed, or Cancelled. No action taken.";
    private const string NotFoundMessage = "No job found with that id.";
    private const string NotCancellableMessage = "Job type does not support cancellation. Atomic mutations (renames, deletes, dependency indexing, URL corrections, cleanup jobs) must run to completion to keep the database consistent.";
    private const string UnknownOutcomeMessage = "Unknown outcome.";

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
