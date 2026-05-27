// VersionFilterKind.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
