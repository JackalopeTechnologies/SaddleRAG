// JobTypeCapabilities.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models.Monitor;

/// <summary>
///     Per-<see cref="JobType" /> capability flags surfaced to callers
///     and the monitor UI. Single source of truth so MCP tools, the
///     unified job view, and the front-end agree on which jobs expose
///     a cancel button.
/// </summary>
public static class JobTypeCapabilities
{
    /// <summary>
    ///     Whether a job of this type supports cooperative cancellation
    ///     via <c>cancel_job</c>. Streaming/idempotent pipelines (scrape,
    ///     dry-run, rechunk, reembed, rescrub) are cancellable; atomic
    ///     mutations (renames, deletes, dependency indexing, URL
    ///     correction, cleanup jobs) are not — interrupting them mid-flight
    ///     would leave the database in a partially-mutated state.
    /// </summary>
    public static bool IsCancellable(this JobType type)
    {
        bool res = type switch
        {
            JobType.Scrape => true,
            JobType.DryRunScrape => true,
            JobType.Rechunk => true,
            JobType.Reembed => true,
            JobType.Rescrub => true,
            _ => false
        };
        return res;
    }
}
