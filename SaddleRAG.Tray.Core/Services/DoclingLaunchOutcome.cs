// DoclingLaunchOutcome.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>What a launch attempt concluded.</summary>
public enum DoclingLaunchOutcome
{
    /// <summary>Docling already answered its health endpoint; nothing was started.</summary>
    AlreadyRunning,

    /// <summary>No Docling command is registered. SaddleRAG never guesses a path.</summary>
    NotRegistered,

    /// <summary>The registered command was started and Docling became healthy.</summary>
    Ready,

    /// <summary>The command started but health never answered inside the bounded wait.</summary>
    Timeout,

    /// <summary>The registered command could not be started.</summary>
    Failed
}
