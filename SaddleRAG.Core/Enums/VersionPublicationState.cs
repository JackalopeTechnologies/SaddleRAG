// VersionPublicationState.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Publication lifecycle for a library version. <see cref="Published" />
///     intentionally remains the zero value so records written before this
///     field existed deserialize as published.
/// </summary>
public enum VersionPublicationState
{
    Published = 0,
    Building = 1,
    Failed = 2
}
