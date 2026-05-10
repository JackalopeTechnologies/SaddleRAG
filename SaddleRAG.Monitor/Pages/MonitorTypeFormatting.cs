// MonitorTypeFormatting.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using MudBlazor;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Monitor.Pages;

internal static class MonitorTypeFormatting
{
    public static Color TypeColor(JobType type)
    {
        return type switch
            {
                JobType.Scrape => Color.Info,
                JobType.DryRunScrape => Color.Info,
                JobType.IndexProjectDependencies => Color.Info,
                JobType.Rechunk => Color.Primary,
                JobType.Rescrub => Color.Primary,
                JobType.SubmitUrlCorrection => Color.Primary,
                JobType.RenameLibrary => Color.Warning,
                JobType.DeleteVersion => Color.Warning,
                JobType.DeleteLibrary => Color.Warning,
                JobType.CleanupAuditLog => Color.Warning,
                JobType.CleanupJobs => Color.Warning,
                JobType.CleanupOrphans => Color.Warning,
                JobType.Unknown => Color.Default,
                var _ => Color.Default
            };
    }

    public static string TypeLabel(JobType type)
    {
        return type switch
            {
                JobType.Scrape => LabelScrape,
                JobType.DryRunScrape => LabelDryRun,
                JobType.Rechunk => LabelRechunk,
                JobType.Rescrub => LabelRescrub,
                JobType.RenameLibrary => LabelRename,
                JobType.DeleteVersion => LabelDeleteVersion,
                JobType.DeleteLibrary => LabelDeleteLibrary,
                JobType.IndexProjectDependencies => LabelIndexDeps,
                JobType.SubmitUrlCorrection => LabelUrlFix,
                JobType.CleanupAuditLog => LabelCleanupAudit,
                JobType.CleanupJobs => LabelCleanupJobs,
                JobType.CleanupOrphans => LabelCleanupOrphans,
                JobType.Unknown => LabelUnknown,
                var _ => type.ToString()
            };
    }

    public static bool IsCrawlerJob(JobType type)
    {
        return type is JobType.Scrape or JobType.DryRunScrape;
    }

    private const string LabelScrape = "Scrape";
    private const string LabelDryRun = "Dry-run";
    private const string LabelRechunk = "Rechunk";
    private const string LabelRescrub = "Rescrub";
    private const string LabelRename = "Rename";
    private const string LabelDeleteVersion = "Delete version";
    private const string LabelDeleteLibrary = "Delete library";
    private const string LabelIndexDeps = "Index deps";
    private const string LabelUrlFix = "URL fix";
    private const string LabelCleanupAudit = "Cleanup audit";
    private const string LabelCleanupJobs = "Cleanup jobs";
    private const string LabelCleanupOrphans = "Cleanup orphans";
    private const string LabelUnknown = "(unknown)";
}
