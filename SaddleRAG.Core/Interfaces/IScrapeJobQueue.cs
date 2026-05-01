// IScrapeJobQueue.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
