// DirectoryVersionClaimStatus.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Enums;

/// <summary>Outcome of an atomic same-date directory-version claim.</summary>
public enum DirectoryVersionClaimStatus
{
    Acquired = 0,
    AlreadyPublished = 1,
    InProgress = 2
}
