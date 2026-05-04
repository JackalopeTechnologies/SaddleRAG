// HostBucket.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models;

/// <summary>
///     A single hostname bucket from the per-library chunk distribution.
///     Count is the number of chunks (not pages) sourced from this host.
/// </summary>
public sealed record HostBucket(string Host, int Count);
