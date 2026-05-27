// ImportProgress.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Packaging;

/// <summary>
///     Progress snapshot emitted during a bundle import operation.
/// </summary>
public sealed record ImportProgress
{
    public required string CurrentVersion { get; init; }
    public required string CurrentStep { get; init; }
    public required int VersionIndex { get; init; }
    public required int TotalVersions { get; init; }
}
