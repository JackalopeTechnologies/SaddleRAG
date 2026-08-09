// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>Crash-recovery checkpoint for an in-progress rename.</summary>
public enum LibraryRenameOperationState
{
    Applying,
    MongoCommitted,
    VectorCommitted
}
