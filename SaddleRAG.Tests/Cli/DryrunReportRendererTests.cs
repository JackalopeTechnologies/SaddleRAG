// DryrunReportRendererTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Cli.Handlers;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Tests.Cli;

/// <summary>
///     Pins the saddlerag-cli dryrun output sections. Operators grep for
///     the section headers ("Pages by host:", "Out-of-scope depth
///     distribution:", "Fetch errors (...)", etc.) — a silent rename here
///     would break their pipelines. Tests cover: the always-present
///     headers + counts, the host/depth sorted-by-count rendering, the
///     HitMaxPagesLimit warning, the error grouping with first-5 preview
///     + "and N more" tail, and the pending-URLs section.
/// </summary>
public sealed class DryrunReportRendererTests
{
    private static DryRunReport NewReport(IReadOnlyDictionary<string, int>? pagesByHost = null,
                                          IReadOnlyDictionary<int, int>? depthDistribution = null,
                                          IReadOnlyList<DryRunFetchError>? errors = null,
                                          IReadOnlyList<string>? samplePendingUrls = null,
                                          IReadOnlyList<string>? gitHubReposToClone = null,
                                          bool hitMaxPagesLimit = false) =>
        new DryRunReport
            {
                TotalPages = 100,
                InScopePages = 80,
                OutOfScopePages = 20,
                DepthLimitedSkips = 0,
                FilteredSkips = 0,
                FetchErrors = errors?.Count ?? 0,
                DepthDistribution = depthDistribution ?? new Dictionary<int, int>(),
                PagesByHost = pagesByHost ?? new Dictionary<string, int>(),
                GitHubReposToClone = gitHubReposToClone ?? [],
                SamplePages = [],
                Errors = errors ?? [],
                ElapsedTime = TimeSpan.FromSeconds(12.5),
                HitMaxPagesLimit = hitMaxPagesLimit,
                PagesRemainingInQueue = 0,
                SamplePendingUrls = samplePendingUrls ?? [],
                DetectedRenderMode = RenderMode.Unknown,
                MedianContentNodeDelta = -1,
                LoadWaitRecommended = true,
                CategoryHistogram = new Dictionary<DocCategory, int>(),
                StageTimings = StageTimings.Empty
            };

    private static DryRunFetchError Error(string url, string kind, string message) =>
        new DryRunFetchError { Url = url, HttpStatus = 500, ErrorKind = kind, Message = message };

    [Fact]
    public void RenderEmitsAllSevenAlwaysPresentHeaderLines()
    {
        var output = new StringWriter();
        DryrunReportRenderer.Render(NewReport(), maxPagesLimit: 1000, output);

        var rendered = output.ToString();
        Assert.Contains("=== Dry Run Report (12.5s) ===", rendered);
        Assert.Contains("Total pages fetched: 100", rendered);
        Assert.Contains("In-scope:    80", rendered);
        Assert.Contains("Out-of-scope: 20", rendered);
        Assert.Contains("Skipped (filtered): 0", rendered);
        Assert.Contains("Skipped (depth limit): 0", rendered);
        Assert.Contains("Fetch errors: 0", rendered);
        Assert.Contains("Pages still in queue at end: 0", rendered);
        Assert.Contains("Pages by host:", rendered);
        Assert.Contains("Out-of-scope depth distribution:", rendered);
    }

    [Fact]
    public void RenderPrintsMaxPagesLimitWarningWhenHit()
    {
        var report = NewReport(hitMaxPagesLimit: true) with { TotalPages = 1000, PagesRemainingInQueue = 500 };
        var output = new StringWriter();

        DryrunReportRenderer.Render(report, maxPagesLimit: 1000, output);

        var rendered = output.ToString();
        Assert.Contains("** HIT MaxPages limit (1000) — actual crawl would have 1500+ pages **", rendered);
    }

    [Fact]
    public void RenderPagesByHostSortsDescendingByCount()
    {
        var report = NewReport(pagesByHost: new Dictionary<string, int>
                                                {
                                                    ["small.example"] = 1,
                                                    ["big.example"] = 50,
                                                    ["mid.example"] = 10
                                                }
                              );
        var output = new StringWriter();

        DryrunReportRenderer.Render(report, maxPagesLimit: 1000, output);

        var lines = output.ToString().Split('\n');
        var bigIdx = Array.FindIndex(lines, l => l.Contains("big.example"));
        var midIdx = Array.FindIndex(lines, l => l.Contains("mid.example"));
        var smallIdx = Array.FindIndex(lines, l => l.Contains("small.example"));
        Assert.True(bigIdx < midIdx);
        Assert.True(midIdx < smallIdx);
    }

    [Fact]
    public void RenderErrorsGroupedByKindWithFirstFivePreviewAndAndNMoreTail()
    {
        var errors = new List<DryRunFetchError>();
        for(var i = 0; i < 8; i++)
            errors.Add(Error($"https://example/{i}", "Http500", $"err-{i}"));

        var report = NewReport(errors: errors);
        var output = new StringWriter();

        DryrunReportRenderer.Render(report, maxPagesLimit: 1000, output);

        var rendered = output.ToString();
        Assert.Contains("Fetch errors (8):", rendered);
        Assert.Contains("[8] Http500", rendered);
        Assert.Contains("err-0", rendered);
        Assert.Contains("err-4", rendered);
        // ErrorPreviewCount is 5, so err-5/6/7 fall into the "... and 3 more" tail.
        Assert.Contains("... and 3 more", rendered);
        Assert.DoesNotContain("err-5", rendered);
    }

    [Fact]
    public void RenderOmitsErrorSectionWhenNoErrors()
    {
        var output = new StringWriter();
        DryrunReportRenderer.Render(NewReport(), maxPagesLimit: 1000, output);

        Assert.DoesNotContain("Fetch errors (", output.ToString());
    }

    [Fact]
    public void RenderPrintsGitHubReposSectionWhenAnyReposPresent()
    {
        var report = NewReport(gitHubReposToClone: ["foo/bar", "qux/baz"]);
        var output = new StringWriter();

        DryrunReportRenderer.Render(report, maxPagesLimit: 1000, output);

        var rendered = output.ToString();
        Assert.Contains("GitHub repos that would be cloned (2):", rendered);
        Assert.Contains("foo/bar", rendered);
        Assert.Contains("qux/baz", rendered);
    }

    [Fact]
    public void RenderPrintsPendingUrlsSectionWhenAnyPresent()
    {
        var report = NewReport(samplePendingUrls: ["https://example/a", "https://example/b"]);
        var output = new StringWriter();

        DryrunReportRenderer.Render(report, maxPagesLimit: 1000, output);

        var rendered = output.ToString();
        Assert.Contains("Sample URLs still in queue (first 2):", rendered);
        Assert.Contains("https://example/a", rendered);
    }

    [Fact]
    public void RenderModeSSRSectionAppearsWhenVoteComplete()
    {
        using var sw = new StringWriter();
        var report = NewReport() with
                     {
                         DetectedRenderMode = RenderMode.SSR,
                         MedianContentNodeDelta = 0,
                         LoadWaitRecommended = false
                     };

        DryrunReportRenderer.Render(report, maxPagesLimit: 200, sw);
        string output = sw.ToString();

        Assert.Contains("Render mode: SSR", output);
        Assert.Contains("Load-state wait: not needed", output);
        Assert.Contains("Median content-node delta: 0", output);
    }

    [Fact]
    public void RenderModeSPASectionAppearsWhenVoteComplete()
    {
        using var sw = new StringWriter();
        var report = NewReport() with
                     {
                         DetectedRenderMode = RenderMode.SPA,
                         MedianContentNodeDelta = 28,
                         LoadWaitRecommended = true
                     };

        DryrunReportRenderer.Render(report, maxPagesLimit: 200, sw);
        string output = sw.ToString();

        Assert.Contains("Render mode: SPA", output);
        Assert.Contains("Load-state wait: required", output);
        Assert.Contains("Median content-node delta: 28", output);
    }

    [Fact]
    public void RenderModeUnknownSectionAppearsWhenVoteIncomplete()
    {
        using var sw = new StringWriter();
        var report = NewReport();

        DryrunReportRenderer.Render(report, maxPagesLimit: 200, sw);
        string output = sw.ToString();

        Assert.Contains("Render mode: Unknown", output);
        Assert.Contains("(fewer than 5 pages sampled)", output);
    }
}
