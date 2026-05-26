// BundleLibraryInfo.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Packaging;

/// <summary>
///     Library identity fields embedded in the bundle manifest.
/// </summary>
public sealed record BundleLibraryInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Hint { get; init; }
}
