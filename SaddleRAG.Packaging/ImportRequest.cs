// ImportRequest.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Packaging;

/// <summary>
///     Parameters for a bundle import operation.
/// </summary>
public sealed record ImportRequest
{
    public required string BundlePath { get; init; }
    public bool Overwrite { get; init; }
    public bool Compact { get; init; }
}
