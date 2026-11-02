// ComponentActionStatus.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>How an operator's start, stop or restart of one component ended.</summary>
public enum ComponentActionStatus
{
    /// <summary>The component reached the requested state.</summary>
    Succeeded,

    /// <summary>The component was already in the requested state; nothing was done.</summary>
    AlreadyInState,

    /// <summary>The operator declined, either the tray's confirmation or the Windows administrator prompt.</summary>
    Cancelled,

    /// <summary>The tray cannot control this component: it runs on another computer, or has no service or address.</summary>
    NotControllable,

    /// <summary>Another component action was still running, so this one was not started.</summary>
    Busy,

    /// <summary>Windows or the tray refused: the process belongs to another account, or the install folder is unsafe.</summary>
    Denied,

    /// <summary>The component did not reach the requested state in the time allowed.</summary>
    TimedOut,

    /// <summary>The action could not be carried out; the message says why.</summary>
    Failed
}
