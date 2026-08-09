// DocumentRevisionState.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Lifecycle state for one manually acquired document revision.
/// </summary>
public enum DocumentRevisionState
{
    Candidate = 0,
    Published = 1,
    Failed = 2,
    Cancelled = 3
}
