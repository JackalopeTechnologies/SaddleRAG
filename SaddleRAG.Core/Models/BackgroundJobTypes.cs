// BackgroundJobTypes.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Core.Models;

/// <summary>
///     String constants for <see cref="BackgroundJobRecord.JobType"/>.
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
}
