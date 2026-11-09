// MonitorTestRecovery.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Options;
using SaddleRAG.Database;
using SaddleRAG.Tests.Database;

#endregion

namespace SaddleRAG.Tests.Monitor;

/// <summary>Real <see cref="DatabaseRecovery" /> instances for monitor page tests.</summary>
internal static class MonitorTestRecovery
{
    /// <summary>Nothing is known down, so every call returns at once without touching the service.</summary>
    public static DatabaseRecovery Healthy() => Create(MongoServiceState.Running, isKnownDown: false);

    /// <summary>
    ///     A local database known down whose service is not installed, so every attempt fails
    ///     and a read that then finds the database unreachable shows the restart report.
    /// </summary>
    public static DatabaseRecovery Failing() => Create(MongoServiceState.NotInstalled, isKnownDown: true);

    /// <summary>
    ///     A local database known down whose service is not installed, so attempts fail until
    ///     <paramref name="recover" /> runs; after that nothing is known down and no report is added.
    /// </summary>
    public static DatabaseRecovery Recoverable(out Action recover)
    {
        IReadOnlyList<IRecoverableDatabase> current = [new RecoveryFakeDatabase(isLocal: true, isKnownDown: true)];
        recover = () => current = [];
        return new DatabaseRecovery(() => current,
                                    new RecoveryFakeServiceControl(MongoServiceState.NotInstalled),
                                    Options.Create(new SaddleRagDbSettings()),
                                    TimeProvider.System,
                                    new RecordingLogger<DatabaseRecovery>(),
                                    RecoveryTestTimings.Short);
    }

    /// <summary>The database context factory the pages use to judge whether a read failure is an outage.</summary>
    public static SaddleRagDbContextFactory ContextFactory() =>
        new SaddleRagDbContextFactory(Options.Create(new SaddleRagDbSettings()));

    private static DatabaseRecovery Create(MongoServiceState serviceState, bool isKnownDown)
    {
        IReadOnlyList<IRecoverableDatabase> databases = [new RecoveryFakeDatabase(isLocal: true, isKnownDown)];
        return new DatabaseRecovery(() => databases,
                                    new RecoveryFakeServiceControl(serviceState),
                                    Options.Create(new SaddleRagDbSettings()),
                                    TimeProvider.System,
                                    new RecordingLogger<DatabaseRecovery>(),
                                    RecoveryTestTimings.Short);
    }
}
