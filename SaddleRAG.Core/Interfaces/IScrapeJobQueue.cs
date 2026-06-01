// IScrapeJobQueue.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Models;

#endregion


#pragma warning disable STR0010 // Interface methods cannot validate parameters


namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Enqueues scrape jobs for background processing.
/// </summary>
public interface IScrapeJobQueue

{
    /// <summary>
    ///     Queue a scrape job and return its identifier immediately.
    /// </summary>
    Task<string> QueueAsync(ScrapeJob job, string? profile = null, CancellationToken ct = default);
}
