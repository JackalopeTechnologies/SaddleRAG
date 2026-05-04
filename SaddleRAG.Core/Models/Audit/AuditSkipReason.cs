// AuditSkipReason.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
