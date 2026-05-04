// AuditContext.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models.Audit;

/// <summary>
///     Identifies the scrape job and library version for an audit event.
/// </summary>
public sealed record AuditContext
{
    /// <summary>
    ///     The scrape job identifier.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    ///     The library being scraped.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     The version being scraped.
    /// </summary>
    public required string Version { get; init; }
}
