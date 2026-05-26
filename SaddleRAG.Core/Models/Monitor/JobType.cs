// JobType.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

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
    Reembed,
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
