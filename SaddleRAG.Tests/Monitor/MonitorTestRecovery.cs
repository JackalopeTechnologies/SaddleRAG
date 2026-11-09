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

    /// <summary>A local database known down whose service is not installed, so every call fails with the outage message.</summary>
    public static DatabaseRecovery Failing() => Create(MongoServiceState.NotInstalled, isKnownDown: true);

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
