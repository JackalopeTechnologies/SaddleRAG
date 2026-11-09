// MonitorDatabaseGate.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     The one step every monitor page takes around a database read: ask
///     <see cref="DatabaseRecovery" /> to bring a stopped local MongoDB back, run the read,
///     and turn a database outage from either step into the message the page shows instead
///     of an unhandled circuit error.
/// </summary>
public static class MonitorDatabaseGate
{
    /// <summary>
    ///     Returns null when the read ran, or the outage message to show the user when the
    ///     restart failed (or its failure pause applies) or the read itself found the database
    ///     unreachable, including a driver connection failure or a timeout while a database is
    ///     known unreachable. Any other failure, and cancellation, still throw.
    /// </summary>
    public static async Task<string?> RunAsync(DatabaseRecovery? recovery,
                                               SaddleRagDbContextFactory? factory,
                                               Func<Task> read,
                                               CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(read);
        string? outage = null;
        try
        {
            await recovery.EnsureAvailableAsync(ct);
            await read();
        }
        catch(Exception ex) when(ex is not OperationCanceledException
                                 && DatabaseFailure.AsUnavailable(ex, factory.GetUnreachableEndpoints()) is { } unavailable)
        {
            outage = unavailable.Message;
        }

        return outage;
    }
}
