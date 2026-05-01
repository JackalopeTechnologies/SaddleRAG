// IBackgroundJobRunner.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

using SaddleRAG.Core.Models;

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Queues a generic background job and returns the job id immediately.
/// </summary>
public interface IBackgroundJobRunner
{
    /// <summary>
    ///     Persist <paramref name="jobRecord"/>, fire off background execution,
    ///     and return its id. The runner owns the full lifecycle
    ///     (Queued → Running → Completed/Failed/Cancelled).
    /// </summary>
    Task<string> QueueAsync(
        BackgroundJobRecord jobRecord,
        Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task> execute,
        CancellationToken ct = default);
}
