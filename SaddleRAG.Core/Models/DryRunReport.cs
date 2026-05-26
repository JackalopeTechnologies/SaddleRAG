// DryRunReport.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

using SaddleRAG.Core.Enums;

namespace SaddleRAG.Core.Models;

/// <summary>
///     Result of a dry-run crawl. Shows what a real ingestion
///     would have stored, without actually writing anything.
/// </summary>
public record DryRunReport
{
    public required int TotalPages { get; init; }
    public required int InScopePages { get; init; }
    public required int OutOfScopePages { get; init; }
    public required int DepthLimitedSkips { get; init; }
    public required int FilteredSkips { get; init; }
    public required int FetchErrors { get; init; }

    /// <summary>
    ///     Distribution of out-of-scope depth values.
    ///     Key = depth, Value = number of pages at that depth.
    /// </summary>
    public required IReadOnlyDictionary<int, int> DepthDistribution { get; init; }

    public required IReadOnlyDictionary<string, int> PagesByHost { get; init; }

    /// <summary>
    ///     GitHub repositories that would be cloned (owner/repo strings).
    /// </summary>
    public required IReadOnlyList<string> GitHubReposToClone { get; init; }

    /// <summary>
    ///     Sample of URLs visited (first N for inspection).
    /// </summary>
    public required IReadOnlyList<DryRunPageEntry> SamplePages { get; init; }

    /// <summary>
    ///     Details of every page that failed to fetch, with error message
    ///     and HTTP status (or 0 if no response).
    /// </summary>
    public required IReadOnlyList<DryRunFetchError> Errors { get; init; }

    public required TimeSpan ElapsedTime { get; init; }
    public required bool HitMaxPagesLimit { get; init; }

    /// <summary>
    ///     Reserved for future use. The streaming dry-run path does not currently
    ///     surface pending-queue depth and always emits 0.
    /// </summary>
    public required int PagesRemainingInQueue { get; init; }

    /// <summary>
    ///     Reserved for future use. The streaming dry-run path does not currently
    ///     surface pending URLs and always emits empty.
    /// </summary>
    public required IReadOnlyList<string> SamplePendingUrls { get; init; }

    /// <summary>
    ///     Render mode detected by sampling the first pages of the crawl.
    ///     <see cref="RenderMode.Unknown" /> if fewer than 5 pages were fetched.
    /// </summary>
    public required RenderMode DetectedRenderMode { get; init; }

    /// <summary>
    ///     Median delta in substantial content nodes (elements with more than
    ///     7 rendered words) between DOMContentLoaded and LoadState.Load,
    ///     across the sample pages. -1 when vote is not complete.
    /// </summary>
    public required int MedianContentNodeDelta { get; init; }

    /// <summary>
    ///     Whether the Load-state wait is recommended for this site.
    ///     False for SSR sites — skipping it saves 4–5 seconds per page.
    /// </summary>
    public required bool LoadWaitRecommended { get; init; }

    /// <summary>
    ///     Number of pages per <see cref="DocCategory" /> resolved by the
    ///     classifier during the dry run. Empty when no pages were
    ///     classified (e.g. crawl returned zero pages).
    /// </summary>
    public required IReadOnlyDictionary<DocCategory, int> CategoryHistogram { get; init; }

    /// <summary>
    ///     Per-stage millisecond totals and sample counts observed during
    ///     the dry run. <see cref="StageTimings.Empty" /> when no pages
    ///     flowed through a stage.
    /// </summary>
    public required StageTimings StageTimings { get; init; }

    /// <summary>
    ///     Non-null when the crawler swapped from the SSR navigator to the
    ///     SPA navigator mid-run. Carries the framework signal and reason
    ///     text. Null on SSR-only runs. See <see cref="NavigatorEscalation" />.
    /// </summary>
    public NavigatorEscalation? Escalation { get; init; }
}
