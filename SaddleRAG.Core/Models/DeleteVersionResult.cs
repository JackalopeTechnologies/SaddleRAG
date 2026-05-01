// DeleteVersionResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Outcome of a single-version delete: how many version rows were
///     removed, whether the parent Library row was cascade-deleted
///     (because no versions remained), and the new currentVersion if
///     one had to be repointed.
/// </summary>
public sealed record DeleteVersionResult(long VersionsDeleted,
                                         bool LibraryRowDeleted,
                                         string? CurrentVersionRepointedTo);
