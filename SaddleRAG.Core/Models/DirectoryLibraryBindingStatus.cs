// DirectoryLibraryBindingStatus.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>Whether a registered directory can be scanned on this machine.</summary>
public enum DirectoryLibraryBindingStatus
{
    Bound = 0,
    Unbound = 1,
    Unavailable = 2
}
