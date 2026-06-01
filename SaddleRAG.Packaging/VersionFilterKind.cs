// VersionFilterKind.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Packaging;

/// <summary>
///     Discriminates the three resolution modes supported by <see cref="VersionFilter"/>.
/// </summary>
public enum VersionFilterKind
{
    Current = 0,
    All = 1,
    Explicit = 2
}
