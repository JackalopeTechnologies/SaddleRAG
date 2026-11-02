// InstallLocationScope.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>Which object's permissions are being checked; each has its own set of dangerous rights.</summary>
public enum InstallLocationScope
{
    /// <summary>The elevated script file itself.</summary>
    Script,

    /// <summary>The folder holding the script.</summary>
    Folder,

    /// <summary>
    ///     Any folder above that folder, up to the volume root: whoever can delete or take over one
    ///     of them could swap the install folder for another.
    /// </summary>
    Ancestor
}
