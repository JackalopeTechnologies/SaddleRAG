// BackgroundJobTypes.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models;

/// <summary>
///     String constants for <see cref="BackgroundJobRecord.JobType" />.
/// </summary>
public static class BackgroundJobTypes
{
    /// <summary>
    ///     Re-run the chunker over pages already stored for a (library, version).
    /// </summary>
    public const string Rechunk = "rechunk";

    /// <summary>
    ///     Rename a library across all collections.
    /// </summary>
    public const string RenameLibrary = "rename_library";

    /// <summary>
    ///     Delete a single (library, version) from all collections.
    /// </summary>
    public const string DeleteVersion = "delete_version";

    /// <summary>
    ///     Delete an entire library (all versions) from all collections.
    /// </summary>
    public const string DeleteLibrary = "delete_library";

    /// <summary>
    ///     Dry-run scrape to estimate page count without ingesting.
    /// </summary>
    public const string DryRunScrape = "dryrun_scrape";

    /// <summary>
    ///     Index the calling project's dependencies from the package manifest.
    /// </summary>
    public const string IndexProjectDependencies = "index_project_dependencies";

    /// <summary>
    ///     Submit a URL correction for a page in an existing library version.
    /// </summary>
    public const string SubmitUrlCorrection = "submit_url_correction";

    /// <summary>
    ///     Manually purge ScrapeAuditLog rows for a single scrape job.
    ///     Audit rows are also auto-purged after 30 days via the TTL index;
    ///     this tool exists for early eviction of large debugging logs.
    /// </summary>
    public const string CleanupAuditLog = "cleanup_audit_log";

    /// <summary>
    ///     Manually delete job tracking rows across ScrapeJobs, BackgroundJobs,
    ///     and RescrubJobs collections by filter (kind, status, library, version,
    ///     or explicit ids). Cascades to ScrapeAuditLog by default for scrape jobs.
    ///     Job rows are also auto-purged after 30 days from CompletedAt via TTL
    ///     indexes; this tool exists for early eviction.
    /// </summary>
    public const string CleanupJobs = "cleanup_jobs";
}
