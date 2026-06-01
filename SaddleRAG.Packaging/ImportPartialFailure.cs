// ImportPartialFailure.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

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
