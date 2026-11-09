// IRecoverableDatabase.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Database;

/// <summary>
///     What <see cref="DatabaseRecovery" /> needs to know about one opened database:
///     where it is, whether it is on this computer, whether it is known to be down,
///     and a way to ask it whether it answers again.
/// </summary>
public interface IRecoverableDatabase
{
    /// <summary>The server address or addresses, as <c>host:port</c>; never the connection string.</summary>
    string Endpoint { get; }

    /// <summary>True when every host in the connection string is this computer.</summary>
    bool IsLocal { get; }

    /// <summary>True when a health check failed or the driver reports every server unreachable.</summary>
    bool IsKnownDown { get; }

    /// <summary>
    ///     Pings the database and records the outcome, so ordinary work resumes after a
    ///     successful check. Throws when the database does not answer.
    /// </summary>
    Task CheckHealthAsync(CancellationToken ct);
}
