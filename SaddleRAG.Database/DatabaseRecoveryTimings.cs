// DatabaseRecoveryTimings.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Database;

/// <summary>
///     The fixed waits <see cref="DatabaseRecovery" /> uses. They are constants, not
///     settings; only tests pass shorter ones.
/// </summary>
/// <param name="RestartWait">How long one restart attempt waits for the database to answer.</param>
/// <param name="FailurePause">After a failed attempt, how long new restart attempts are held back (requests carry on, and one that touches the database reports the stored restart report).</param>
/// <param name="PingTimeout">The limit on one health ping.</param>
/// <param name="PingInterval">The wait between rounds of pings, and between service-state polls.</param>
public sealed record DatabaseRecoveryTimings(TimeSpan RestartWait,
                                             TimeSpan FailurePause,
                                             TimeSpan PingTimeout,
                                             TimeSpan PingInterval)
{
    /// <summary>60 s restart wait, 2 min failure pause, 3 s ping limit, 1 s between pings.</summary>
    public static DatabaseRecoveryTimings Default { get; } =
        new DatabaseRecoveryTimings(TimeSpan.FromSeconds(DefaultRestartWaitSeconds),
                                    TimeSpan.FromMinutes(DefaultFailurePauseMinutes),
                                    TimeSpan.FromSeconds(DefaultPingTimeoutSeconds),
                                    TimeSpan.FromSeconds(DefaultPingIntervalSeconds)
                                   );

    private const int DefaultRestartWaitSeconds = 60;
    private const int DefaultFailurePauseMinutes = 2;
    private const int DefaultPingTimeoutSeconds = 3;
    private const int DefaultPingIntervalSeconds = 1;
}
