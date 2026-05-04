// AuditStatus.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
