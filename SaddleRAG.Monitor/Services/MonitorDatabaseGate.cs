// MonitorDatabaseGate.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     The one step every monitor page takes just before it reads the database: ask
///     <see cref="DatabaseRecovery" /> to bring a stopped local MongoDB back, and turn a
///     failure into the message the page shows instead of an unhandled circuit error.
/// </summary>
public static class MonitorDatabaseGate
{
    /// <summary>
    ///     Returns null when the database can be read (nothing was down, or it was restarted),
    ///     or the outage message to show the user when the restart failed or its failure
    ///     pause is still in effect. Cancelling <paramref name="ct" /> still throws, so a
    ///     page that is closing stops waiting.
    /// </summary>
    public static async Task<string?> TryEnsureAvailableAsync(DatabaseRecovery? recovery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        string? outage = null;
        try
        {
            await recovery.EnsureAvailableAsync(ct);
        }
        catch(DatabaseUnavailableException ex)
        {
            outage = ex.Message;
        }

        return outage;
    }
}
