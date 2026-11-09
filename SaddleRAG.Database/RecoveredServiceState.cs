// RecoveredServiceState.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Database;

/// <summary>
///     What <see cref="DatabaseRecovery" /> found the MongoDB service doing during one
///     attempt, so its log and report say what actually happened.
/// </summary>
internal enum RecoveredServiceState
{
    /// <summary>The service was stopped (or finished stopping) and SaddleRAG started it.</summary>
    StartedBySaddleRag,

    /// <summary>The service was already starting; SaddleRAG only waited for it.</summary>
    WasStarting,

    /// <summary>The service was already running (or in another state); SaddleRAG only waited for it.</summary>
    WasRunning
}
