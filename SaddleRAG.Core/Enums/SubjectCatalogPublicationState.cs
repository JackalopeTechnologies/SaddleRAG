// SubjectCatalogPublicationState.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Publication lifecycle for an immutable subject catalog. Published is
///     the zero value so legacy rows remain reusable and deletion-protected.
/// </summary>
public enum SubjectCatalogPublicationState
{
    Published = 0,
    Candidate = 1
}
