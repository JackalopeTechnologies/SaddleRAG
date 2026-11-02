// ServiceActionOutcome.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>How an elevated service action ended.</summary>
public enum ServiceActionOutcome
{
    /// <summary>The script reported every service reached the requested state.</summary>
    Succeeded,

    /// <summary>The operator declined the Windows administrator-approval prompt (UAC).</summary>
    Cancelled,

    /// <summary>A name was refused by the script's allow-list; nothing was changed.</summary>
    NotAllowed,

    /// <summary>A named service is not installed; nothing was changed.</summary>
    NotInstalled,

    /// <summary>A service did not reach the requested state in time, or the tray stopped waiting.</summary>
    TimedOut,

    /// <summary>The script ran and the service control call failed, or it could not be run.</summary>
    Failed,

    /// <summary>The installed script is not present beside the tray. Tray-only; the script never returns this.</summary>
    ScriptMissing,

    /// <summary>
    ///     The install folder can be changed by non-administrators, so the tray refused to elevate a
    ///     script that such a user could have replaced. Tray-only; the script never returns this.
    /// </summary>
    UnsafeInstallLocation
}
