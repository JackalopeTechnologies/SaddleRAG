// AuditSkipReason.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models.Audit;

/// <summary>
///     Reason a URL was skipped during a scrape job.
/// </summary>
public enum AuditSkipReason
{
    PatternExclude = 0,
    PatternMissAllowed = 1,
    BinaryExt = 2,
    OffSiteDepth = 3,
    SameHostDepth = 4,
    HostGated = 5,
    AlreadyVisited = 6,
    QueueLimit = 7
}
