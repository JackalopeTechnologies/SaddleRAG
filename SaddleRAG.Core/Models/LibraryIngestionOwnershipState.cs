// LibraryIngestionOwnershipState.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>Lifecycle state of a durable library ingestion-mode reservation.</summary>
public enum LibraryIngestionOwnershipState
{
    Reserved,
    Committed
}
