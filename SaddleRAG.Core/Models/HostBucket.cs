// HostBucket.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     A single hostname bucket from the per-library chunk distribution.
///     Count is the number of chunks (not pages) sourced from this host.
/// </summary>
public sealed record HostBucket(string Host, int Count);
