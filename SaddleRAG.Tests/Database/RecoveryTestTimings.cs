// RecoveryTestTimings.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Tests.Database;

/// <summary>Short recovery timings so tests of the restart wait and failure pause run in well under a second.</summary>
internal static class RecoveryTestTimings
{
    public static readonly DatabaseRecoveryTimings Short =
        new DatabaseRecoveryTimings(RestartWait: TimeSpan.FromMilliseconds(milliseconds: 300),
                                    FailurePause: TimeSpan.FromMilliseconds(milliseconds: 500),
                                    PingTimeout: TimeSpan.FromMilliseconds(milliseconds: 50),
                                    PingInterval: TimeSpan.FromMilliseconds(milliseconds: 20)
                                   );
}
