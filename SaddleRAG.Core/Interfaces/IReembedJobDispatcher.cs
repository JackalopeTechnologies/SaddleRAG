// IReembedJobDispatcher.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Dispatches an already-durable queued re-embed job for background execution.
/// </summary>
public interface IReembedJobDispatcher
{
    /// <summary>
    ///     Schedules <paramref name="record" /> for an atomic durable execution
    ///     claim. Returns false when the same profile/job is already scheduled
    ///     in this process or application shutdown has begun.
    /// </summary>
    bool TryDispatchPersisted(JobRecord record);
}
