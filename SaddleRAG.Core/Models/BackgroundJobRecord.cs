// BackgroundJobRecord.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Core.Models;

/// <summary>
///     Tracks the lifecycle of a single generic background job for status polling.
///     Covers: rechunk, rename_library, delete_version, delete_library,
///     dryrun_scrape, index_project_dependencies, and submit_url_correction.
/// </summary>
public class BackgroundJobRecord
{
    /// <summary>
    ///     Unique job identifier (GUID string).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Discriminator for the operation type. Use <see cref="BackgroundJobTypes" /> constants.
    /// </summary>
    public required string JobType { get; init; }

    /// <summary>
    ///     Database profile this job is operating against. Null uses the default profile.
    /// </summary>
    public string? Profile { get; init; }

    /// <summary>
    ///     Library being operated on. Null for job types that are not library-scoped
    ///     (e.g. <see cref="BackgroundJobTypes.DryRunScrape" /> and
    ///     <see cref="BackgroundJobTypes.IndexProjectDependencies" />).
    /// </summary>
    public string? LibraryId { get; init; }

    /// <summary>
    ///     Library version being operated on. Null for job types that are not
    ///     version-scoped (e.g. <see cref="BackgroundJobTypes.RenameLibrary" />,
    ///     <see cref="BackgroundJobTypes.DeleteLibrary" />,
    ///     <see cref="BackgroundJobTypes.DryRunScrape" />, and
    ///     <see cref="BackgroundJobTypes.IndexProjectDependencies" />).
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    ///     JSON-serialized input parameters, stored for display and diagnostics.
    /// </summary>
    public required string InputJson { get; init; }

    /// <summary>
    ///     Current lifecycle status.
    /// </summary>
    public ScrapeJobStatus Status { get; set; } = ScrapeJobStatus.Queued;

    /// <summary>
    ///     Human-readable pipeline state string, updated at each lifecycle transition.
    /// </summary>
    public string PipelineState { get; set; } = nameof(ScrapeJobStatus.Queued);

    /// <summary>
    ///     Number of items processed so far. Meaning depends on job type:
    ///     chunks for rechunk, packages for index_project_dependencies,
    ///     pages for dryrun_scrape. Binary-status operations leave this at zero.
    /// </summary>
    public int ItemsProcessed { get; set; }

    /// <summary>
    ///     Total items to process. Zero when the job type does not report incremental progress.
    /// </summary>
    public int ItemsTotal { get; set; }

    /// <summary>
    ///     Label describing the unit being counted (e.g. "chunks", "packages", "pages").
    ///     Null for binary-status operations that do not report incremental progress.
    /// </summary>
    public string? ItemsLabel { get; set; }

    /// <summary>
    ///     Error message populated when <see cref="Status" /> is
    ///     <see cref="ScrapeJobStatus.Failed" />.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     JSON-serialized result, populated when <see cref="Status" /> reaches
    ///     <see cref="ScrapeJobStatus.Completed" />. Shape varies by job type.
    /// </summary>
    public string? ResultJson { get; set; }

    /// <summary>
    ///     When the job record was created (UTC).
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    ///     When the job transitioned to <see cref="ScrapeJobStatus.Running" /> (UTC).
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    ///     When the job finished — success, failure, or cancellation (UTC).
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    ///     When incremental progress was last written (UTC).
    /// </summary>
    public DateTime? LastProgressAt { get; set; }

    /// <summary>
    ///     When the job was cancelled, if applicable (UTC).
    /// </summary>
    public DateTime? CancelledAt { get; set; }
}
