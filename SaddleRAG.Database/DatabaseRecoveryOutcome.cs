// DatabaseRecoveryOutcome.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Database;

/// <summary>
///     How one <see cref="DatabaseRecovery" /> attempt ended. The shared attempt returns
///     this instead of throwing, so a failure nobody is still waiting for is not left as
///     an unobserved task exception (which the server would log as a second Error);
///     each waiting caller throws its own exception from it.
/// </summary>
/// <param name="FailureReport">What SaddleRAG tried and how it ended; null when the database answered.</param>
/// <param name="FailureCause">The service-control error behind the failure, if any.</param>
internal sealed record DatabaseRecoveryOutcome(string? FailureReport, Exception? FailureCause)
{
    /// <summary>The database answered again.</summary>
    public static DatabaseRecoveryOutcome Recovered { get; } = new DatabaseRecoveryOutcome(null, null);
}
