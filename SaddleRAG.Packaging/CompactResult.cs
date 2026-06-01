// CompactResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Packaging;

/// <summary>
///     Result of a single <c>compact</c> command on one collection,
///     including before/after storage metrics and elapsed time.
/// </summary>
public sealed record CompactResult(
    string Collection,
    bool Ok,
    long BytesFreed,
    long StorageBefore,
    long StorageAfter,
    long IndexBefore,
    long IndexAfter,
    long ElapsedMs,
    string? Error);
