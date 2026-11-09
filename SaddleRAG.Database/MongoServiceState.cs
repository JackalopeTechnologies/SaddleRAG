// MongoServiceState.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Database;

/// <summary>
///     What Windows reports about a MongoDB service, reduced to the cases SaddleRAG
///     acts on.
/// </summary>
public enum MongoServiceState
{
    /// <summary>No service with that name is installed.</summary>
    NotInstalled,

    /// <summary>The service is installed and stopped.</summary>
    Stopped,

    /// <summary>The service is starting.</summary>
    StartPending,

    /// <summary>The service is running.</summary>
    Running,

    /// <summary>The service is stopping.</summary>
    StopPending,

    /// <summary>Any other state, including paused states.</summary>
    Other,

    /// <summary>This operating system has no Windows services to inspect.</summary>
    Unsupported
}
