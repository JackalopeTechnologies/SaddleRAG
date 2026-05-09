// JobType.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models.Monitor;

/// <summary>
///     Discriminator for jobs surfaced by <see cref="SaddleRAG.Core.Interfaces.IUnifiedJobView" />.
/// </summary>
public enum JobType
{
    Scrape,
    DryRunScrape,
    Rechunk,
    Rescrub,
    RenameLibrary,
    DeleteVersion,
    DeleteLibrary,
    IndexProjectDependencies,
    SubmitUrlCorrection,
    CleanupAuditLog,
    CleanupJobs,
    CleanupOrphans,
    Unknown
}
