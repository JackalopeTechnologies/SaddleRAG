// DryrunReportRenderer.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Cli.Handlers;

/// <summary>
///     Renders a <see cref="DryRunReport" /> as the user-facing output of
///     <c>saddlerag-cli dryrun</c>. Headers, per-host counts, depth
///     histogram, GitHub-repo list, error groups, pending-URL sample —
///     every section that CLI consumers or operators grep against.
///     Extracted from Program.cs so the section presence + grouping rules
///     are unit-tested rather than guarded by hand.
/// </summary>
public static class DryrunReportRenderer
{
    /// <summary>
    ///     Write the full dry-run report to <paramref name="output" />.
    ///     <paramref name="maxPagesLimit" /> is the configured MaxPages
    ///     limit, surfaced verbatim in the warning line when the crawl hit
    ///     the cap (so the user can see what they need to raise).
    /// </summary>
    public static int Render(DryRunReport report, int maxPagesLimit, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine($"=== Dry Run Report ({report.ElapsedTime.TotalSeconds:F1}s) ===");
        output.WriteLine($"Total pages fetched: {report.TotalPages}");
        output.WriteLine($"  In-scope:    {report.InScopePages}");
        output.WriteLine($"  Out-of-scope: {report.OutOfScopePages}");
        output.WriteLine($"Skipped (filtered): {report.FilteredSkips}");
        output.WriteLine($"Skipped (depth limit): {report.DepthLimitedSkips}");
        output.WriteLine($"Fetch errors: {report.FetchErrors}");
        output.WriteLine($"Pages still in queue at end: {report.PagesRemainingInQueue}");
        if (report.HitMaxPagesLimit)
        {
            output
                .WriteLine($"** HIT MaxPages limit ({maxPagesLimit}) — actual crawl would have {report.TotalPages + report.PagesRemainingInQueue}+ pages **"
                          );
        }

        output.WriteLine();
        output.WriteLine("Pages by host:");
        foreach((var host, var count) in report.PagesByHost.OrderByDescending(kv => kv.Value))
            output.WriteLine($"  {host}: {count}");

        output.WriteLine();
        output.WriteLine("Out-of-scope depth distribution:");
        foreach((var depth, var count) in report.DepthDistribution.OrderBy(kv => kv.Key))
            output.WriteLine($"  depth {depth}: {count}");

        if (report.GitHubReposToClone.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"GitHub repos that would be cloned ({report.GitHubReposToClone.Count}):");
            foreach(var repo in report.GitHubReposToClone)
                output.WriteLine($"  {repo}");
        }

        if (report.Errors.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"Fetch errors ({report.Errors.Count}):");
            var grouped = report.Errors.GroupBy(e => e.ErrorKind).OrderByDescending(g => g.Count());
            foreach(var group in grouped)
            {
                output.WriteLine($"  [{group.Count()}] {group.Key}");
                foreach(var err in group.Take(ErrorPreviewCount))
                    output.WriteLine($"    {err.Url} — {err.Message}");
                if (group.Count() > ErrorPreviewCount)
                    output.WriteLine($"    ... and {group.Count() - ErrorPreviewCount} more");
            }
        }

        if (report.SamplePendingUrls.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"Sample URLs still in queue (first {report.SamplePendingUrls.Count}):");
            foreach(var pending in report.SamplePendingUrls)
                output.WriteLine($"  {pending}");
        }

        return 0;
    }

    private const int ErrorPreviewCount = 5;
}
