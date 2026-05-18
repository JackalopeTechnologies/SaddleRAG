// ClassifyStage.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     Second stage of the streaming ingestion pipeline. Consumes
///     <see cref="PageRecord" /> from the crawl stage, classifies each via
///     <see cref="ILlmClassifier.ClassifyAsync" />, persists the updated page
///     when classification succeeded with non-zero confidence, and forwards
///     to the chunk stage. Per-page classification exceptions are logged at
///     warning and the page passes through unmodified with an incremented
///     error count — only stage-level cancellation or non-classifier
///     exceptions terminate the stage.
/// </summary>
internal sealed class ClassifyStage
{
    public ClassifyStage(ILlmClassifier llmClassifier,
                         IPageRepository pageRepository,
                         IMonitorBroadcaster broadcaster,
                         ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(llmClassifier);
        ArgumentNullException.ThrowIfNull(pageRepository);
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(logger);
        mLlmClassifier = llmClassifier;
        mPageRepository = pageRepository;
        mBroadcaster = broadcaster;
        mLogger = logger;
    }

    private readonly IMonitorBroadcaster mBroadcaster;
    private readonly ILlmClassifier mLlmClassifier;
    private readonly ILogger mLogger;
    private readonly IPageRepository mPageRepository;

    /// <summary>
    ///     Run the classify stage to completion, cancellation, or fatal error.
    ///     Always completes <paramref name="output" /> in the finally block
    ///     so the chunk stage's <c>ReadAllAsync</c> terminates even if no
    ///     pages flowed through.
    /// </summary>
    public async Task RunAsync(ScrapeJob job,
                               ChannelReader<PageRecord> input,
                               ChannelWriter<PageRecord> output,
                               ScrapeJobRecord progress,
                               Action<ScrapeJobRecord>? onProgress,
                               CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(cts);

        try
        {
            await foreach(var page in input.ReadAllAsync(cts.Token))
            {
                var classified = await ClassifyPageAsync(page, job.LibraryHint, () => progress.IncrementErrorCount());
                await output.WriteAsync(classified, cts.Token);
                progress.PagesClassified++;
                onProgress?.Invoke(progress);
                mBroadcaster.RecordPageClassified(progress.Id);
            }
        }
        catch(OperationCanceledException)
        {
            output.TryComplete();
            throw;
        }
        catch(Exception ex)
        {
            mLogger.LogError(ex, "Classify stage fatal error");
            output.TryComplete(ex);
            await cts.CancelAsync();
            throw;
        }
        finally
        {
            output.TryComplete();
        }
    }

    /// <summary>
    ///     Classify a single page using the LLM and upsert if high-confidence.
    ///     Exposed as <c>internal</c> so the orchestrator's single-page ingest
    ///     path can reuse the exact same classify-and-absorb semantics that
    ///     the streaming stage applies per page. <paramref name="onError" />
    ///     is invoked once on any classifier exception (the streaming path
    ///     wires it to <c>progress.IncrementErrorCount</c>; the single-page
    ///     path passes <c>null</c> because there is no progress object).
    /// </summary>
    internal async Task<PageRecord> ClassifyPageAsync(PageRecord page, string libraryHint, Action? onError = null)
    {
        var sw = Stopwatch.StartNew();
        PageRecord result;
        var category = DocCategory.Unclassified;
        var confidence = 0f;
        try
        {
            (category, confidence) = await mLlmClassifier.ClassifyAsync(page, libraryHint);
            if (category != DocCategory.Unclassified && confidence > 0)
            {
                result = page with { Category = category };
                await mPageRepository.UpsertPageAsync(result);
            }
            else
                result = page;
        }
        catch(Exception ex)
        {
            mLogger.LogWarning(ex, "LLM classification failed for {Url}, passing as Unclassified", page.Url);
            onError?.Invoke();
            result = page;
        }

        long classifyMs = sw.ElapsedMilliseconds;
        mLogger.LogInformation("Classified {Url} in {ClassifyMs}ms category={Category} confidence={Confidence:F2}",
                               page.Url,
                               classifyMs,
                               category,
                               confidence
                              );

        return result;
    }
}
