// CancelScrapeOutcome.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Outcome of a cancel_job MCP tool call. Signalled means an
///     active runner's CancellationTokenSource was triggered;
///     OrphanCleanedUp means the DB row was updated directly because
///     no active runner existed (process restart). AlreadyTerminal
///     and NotFound are no-op cases. NotCancellable is a refusal:
///     the job type does not support cooperative cancellation
///     (deletes, renames, cleanups, project indexing, URL corrections)
///     and the caller should wait for natural completion.
/// </summary>
public enum CancelScrapeOutcome
{
    Signalled,
    OrphanCleanedUp,
    AlreadyTerminal,
    NotFound,
    NotCancellable
}
