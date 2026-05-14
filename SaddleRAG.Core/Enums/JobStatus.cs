// JobStatus.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Lifecycle status of a job recorded in the unified <c>jobs</c>
///     MongoDB collection. Shared across every <c>JobType</c> (scrape,
///     rescrub, reembed, rechunk, cleanup, etc.) — the previous
///     per-pipeline collections each carried an identical copy of this
///     enum under the name <c>ScrapeJobStatus</c>; consolidating to one
///     name lets the unified <c>JobRecord</c> use a single type.
/// </summary>
public enum JobStatus
{
    /// <summary>Job created but not yet started.</summary>
    Queued = 0,

    /// <summary>Job is currently running.</summary>
    Running = 1,

    /// <summary>Job finished successfully.</summary>
    Completed = 2,

    /// <summary>Job stopped due to an error.</summary>
    Failed = 3,

    /// <summary>Job was cancelled.</summary>
    Cancelled = 4
}
