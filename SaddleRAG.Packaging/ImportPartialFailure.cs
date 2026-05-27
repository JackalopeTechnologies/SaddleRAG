// ImportPartialFailure.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Packaging;

/// <summary>
///     Describes a version that failed to import during an otherwise-successful
///     import operation.
/// </summary>
public sealed record ImportPartialFailure
{
    public required string Version { get; init; }
    public required string Reason { get; init; }
}
