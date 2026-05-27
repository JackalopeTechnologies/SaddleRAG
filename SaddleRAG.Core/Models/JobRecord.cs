// JobRecord.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Core.Models;

/// <summary>
///     Unified record for every job tracked in the SaddleRAG database.
///     Replaces the four legacy per-pipeline record types
///     (<c>ScrapeJobRecord</c>, <c>RescrubJobRecord</c>,
///     <c>ReembedJobRecord</c>, <c>BackgroundJobRecord</c>) and their
///     corresponding MongoDB collections with a single <c>jobs</c>
///     collection discriminated by <see cref="JobType" />.
///     <para>
///         The schema reduces to three layers: a common envelope shared
///         by every job (id, status, lifecycle timestamps), a generic
///         progress triple
///         (<see cref="ItemsProcessed" />/<see cref="ItemsTotal" />/<see cref="ItemsLabel" />)
///         the monitor UI projects all rows to, and a type-specific
///         payload pair (<see cref="InputJson" />/<see cref="ResultJson" />)
///         the originating pipeline writes and reads via typed wrappers.
///     </para>
///     <para>
///         Scrape is the only job type with a multi-stage pipeline whose
///         intermediate counters the monitor surfaces; its per-stage
///         counts live in the optional <see cref="ScrapeProgress" />
///         sub-document. Every other type leaves
///         <see cref="ScrapeProgress" /> null.
///     </para>
///     <para>
///         <see cref="Status" /> and <see cref="JobType" /> are persisted
///         as MongoDB strings (via class-map registration in the
///         database layer) so documents are human-readable and immune to
///         enum-reorder drift. Core has no MongoDB dependency; the
///         serialization config lives at the database layer.
///     </para>
/// </summary>
public class JobRecord
{
    /// <summary>Unique job identifier (GUID string).</summary>
    public required string Id { get; init; }

    /// <summary>Discriminator: which pipeline / operation produced this record.</summary>
    public required JobType JobType { get; init; }

    /// <summary>Database profile this job operated against. Null = default profile.</summary>
    public string? Profile { get; init; }

    /// <summary>
    ///     Library this job targets. Null for non-library-scoped jobs
    ///     such as <see cref="Monitor.JobType.CleanupOrphans" /> or
    ///     <see cref="Monitor.JobType.IndexProjectDependencies" />.
    /// </summary>
    public string? LibraryId { get; init; }

    /// <summary>
    ///     Library version. Null for non-version-scoped jobs (rename,
    ///     delete-library, dryrun-scrape).
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    ///     JSON-serialized input parameters captured at job-queue time
    ///     for display and diagnostics. Type-specific shape; deserialize
    ///     via the typed wrapper for the originating
    ///     <see cref="JobType" />.
    /// </summary>
    public string? InputJson { get; init; }

    /// <summary>Current lifecycle status.</summary>
    public JobStatus Status { get; set; } = JobStatus.Queued;

    /// <summary>
    ///     Human-readable state string updated at every lifecycle
    ///     transition. Defaults to the <see cref="JobStatus.Queued" />
    ///     enum name; runners may overwrite with finer-grained labels
    ///     (e.g. <c>"FetchingChunks"</c>, <c>"WritingResults"</c>).
    /// </summary>
    public string PipelineState { get; set; } = nameof(JobStatus.Queued);

    /// <summary>
    ///     Number of items the job has processed so far. Unit depends
    ///     on the job type (pages / chunks / packages / etc.) — see
    ///     <see cref="ItemsLabel" />.
    /// </summary>
    public long ItemsProcessed { get; set; }

    /// <summary>
    ///     Total items the job expects to process. Zero when the job
    ///     type does not have a meaningful total ahead of time.
    /// </summary>
    public long ItemsTotal { get; set; }

    /// <summary>
    ///     Display label for the unit being counted in
    ///     <see cref="ItemsProcessed" />/<see cref="ItemsTotal" />
    ///     (<c>"pages"</c>, <c>"chunks"</c>, <c>"packages"</c>, …).
    ///     Null when the job has no incremental progress to report.
    /// </summary>
    public string? ItemsLabel { get; set; }

    /// <summary>
    ///     JSON-serialized result payload populated on transition to
    ///     <see cref="JobStatus.Completed" />. Type-specific shape;
    ///     deserialize via the wrapper for the originating
    ///     <see cref="JobType" />.
    /// </summary>
    public string? ResultJson { get; set; }

    /// <summary>
    ///     Scrape-specific per-stage progress counters. Populated only
    ///     for <see cref="Monitor.JobType.Scrape" /> jobs whose
    ///     orchestrator emits per-stage tick events; null for every
    ///     other job type.
    /// </summary>
    public ScrapeProgress? ScrapeProgress { get; set; }

    /// <summary>
    ///     Count of recoverable errors observed during the job. Today
    ///     only scrape's parallel fetchers increment this (via
    ///     <see cref="IncrementErrorCount" />); other job types leave
    ///     it at zero.
    /// </summary>
    public int ErrorCount { get; set; }

    /// <summary>
    ///     Terminal error message populated when <see cref="Status" />
    ///     is <see cref="JobStatus.Failed" />.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>When the job record was first persisted (UTC).</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>When the job transitioned to <see cref="JobStatus.Running" /> (UTC).</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When the job finished — success, failure, or cancellation (UTC).</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>When incremental progress was last written (UTC).</summary>
    public DateTime? LastProgressAt { get; set; }

    /// <summary>When the job was cancelled, if applicable (UTC).</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>
    ///     Thread-safe increment of <see cref="ErrorCount" />. Used by
    ///     scrape's parallel fetch workers; safe to call from any other
    ///     pipeline too.
    /// </summary>
    public void IncrementErrorCount()
    {
        Interlocked.Increment(ref mErrorCountField);
        ErrorCount = mErrorCountField;
    }

    private int mErrorCountField;
}
