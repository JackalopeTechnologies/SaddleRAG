// ExportRequest.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Packaging;

/// <summary>
///     Parameters for a library export operation.
/// </summary>
public sealed record ExportRequest
{
    public required string LibraryId { get; init; }
    public required VersionFilter Versions { get; init; }
    public required string OutputPath { get; init; }
}
