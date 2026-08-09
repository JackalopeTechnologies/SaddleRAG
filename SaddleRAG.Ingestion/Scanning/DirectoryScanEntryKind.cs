// DirectoryScanEntryKind.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Filesystem item represented by a preview result.</summary>
public enum DirectoryScanEntryKind
{
    Root = 0,
    Directory = 1,
    File = 2
}
