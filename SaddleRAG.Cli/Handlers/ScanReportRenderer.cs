// ScanReportRenderer.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Cli.Handlers;

/// <summary>
///     Renders a <see cref="DependencyIndexReport" /> as the user-facing
///     output of <c>saddlerag-cli scan</c>. Extracted from Program.cs so
///     the header + counts block, the queued section, and the failed
///     section all have unit tests pinning the exact format that
///     downstream automation (CI scripts grepping for "Resolution failed:"
///     etc.) depends on.
/// </summary>
public static class ScanReportRenderer
{
    /// <summary>
    ///     Write the full scan report to <paramref name="output" />.
    ///     Returns 0 always — the scan is non-fatal even when individual
    ///     packages fail resolution; those surface in the Failed section.
    /// </summary>
    public static int Render(DependencyIndexReport report, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine("=== Dependency Scan Report ===");
        output.WriteLine($"Project path:             {report.ProjectPath}");
        output.WriteLine($"Total dependencies found:  {report.TotalDependencies}");
        output.WriteLine($"Filtered out:              {report.FilteredOut}");
        output.WriteLine($"Already cached:            {report.AlreadyCached}");
        output.WriteLine($"Cached (different version): {report.CachedDifferentVersion}");
        output.WriteLine($"Newly queued:              {report.NewlyQueued}");
        output.WriteLine($"Resolution failed:         {report.ResolutionFailed}");

        var queuedPackages = report.Packages.Where(p => p.Status == QueuedStatus).ToList();
        if (queuedPackages.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"Queued for scraping ({queuedPackages.Count}):");
            foreach(var pkg in queuedPackages)
                output.WriteLine($"  {pkg.EcosystemId}/{pkg.PackageId} {pkg.Version} -> {pkg.DocUrl}");
        }

        var failedPackages = report.Packages.Where(p => p.Status == FailedStatus).ToList();
        if (failedPackages.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"Failed ({failedPackages.Count}):");
            foreach(var pkg in failedPackages)
                output.WriteLine($"  {pkg.EcosystemId}/{pkg.PackageId} {pkg.Version} — {pkg.ErrorMessage}");
        }

        return 0;
    }

    internal const string QueuedStatus = "queued";
    internal const string FailedStatus = "failed";
}
