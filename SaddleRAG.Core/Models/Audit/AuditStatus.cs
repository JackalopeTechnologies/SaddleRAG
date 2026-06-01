// AuditStatus.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models.Audit;

/// <summary>
///     Lifecycle status of a URL encountered during a scrape job.
/// </summary>
public enum AuditStatus
{
    Considered = 0,
    Skipped = 1,
    Fetched = 2,
    Failed = 3,
    Indexed = 4
}
