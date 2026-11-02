// ProcessStopOutcome.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>What happened when the tray tried to stop a process.</summary>
public enum ProcessStopOutcome
{
    /// <summary>The process (and the processes it started) exited.</summary>
    Stopped,

    /// <summary>No such process was running, or it exited before it could be stopped.</summary>
    NotFound,

    /// <summary>Windows refused: the process belongs to another user or is elevated.</summary>
    AccessDenied,

    /// <summary>The process did not exit within the time allowed.</summary>
    TimedOut
}