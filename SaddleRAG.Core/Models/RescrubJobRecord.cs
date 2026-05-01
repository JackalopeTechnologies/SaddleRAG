// RescrubJobRecord.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Core.Models;

/// <summary>
///     Tracks the lifecycle of a single rescrub job for status polling.
/// </summary>
public class RescrubJobRecord
{
    /// <summary>
    ///     Unique job identifier (GUID string).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Library being rescrubbed.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     Library version being rescrubbed.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     Database profile this job is reading from and writing to.
    /// </summary>
    public string? Profile { get; init; }

    /// <summary>
    ///     Options submitted with the job.
    /// </summary>
    public required RescrubOptions Options { get; init; }

    /// <summary>
    ///     Current status.
    /// </summary>
    public ScrapeJobStatus Status { get; set; } = ScrapeJobStatus.Queued;

    /// <summary>
    ///     Human-readable pipeline state string.
    /// </summary>
    public string PipelineState { get; set; } = nameof(ScrapeJobStatus.Queued);

    /// <summary>
    ///     Total chunks to process (set when job transitions to Running).
    /// </summary>
    public int ChunksTotal { get; set; }

    /// <summary>
    ///     Chunks examined so far.
    /// </summary>
    public int ChunksProcessed { get; set; }

    /// <summary>
    ///     Chunks whose symbols, qualified name, parser version, or category changed.
    /// </summary>
    public int ChunksChanged { get; set; }

    /// <summary>
    ///     Error message when Status is Failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     Full result populated when Status reaches Completed.
    /// </summary>
    public RescrubResult? Result { get; set; }

    /// <summary>
    ///     When the job was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    ///     When the job started running.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    ///     When the job finished (success, failure, or cancellation).
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    ///     When chunk progress was last recorded.
    /// </summary>
    public DateTime? LastProgressAt { get; set; }

    /// <summary>
    ///     When the job was cancelled, if applicable.
    /// </summary>
    public DateTime? CancelledAt { get; set; }
}
