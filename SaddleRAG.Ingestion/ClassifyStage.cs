// ClassifyStage.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

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
        mPageRepository = pageRepository;
        mBroadcaster = broadcaster;
        mLogger = logger;
        mClassify = (page, hint, mode, ct) => ClassifyLegacyAsync(llmClassifier, page, hint, mode, ct);
    }

    internal ClassifyStage(IngestionPageProcessor processor,
                           IPageRepository pageRepository,
                           IMonitorBroadcaster broadcaster,
                           ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(pageRepository);
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(logger);
        mPageRepository = pageRepository;
        mBroadcaster = broadcaster;
        mLogger = logger;
        mClassify = async (page, hint, mode, ct) =>
                        {
                            PageRecord classified = await processor.ClassifyAsync(
                                                        page,
                                                        hint,
                                                        pageRepository,
                                                        mode == IngestionPersistenceMode.Full
                                                            ? PagePersistenceIntent.UpdateIfClassified
                                                            : PagePersistenceIntent.None,
                                                        ct);
                            float confidence = classified.Category == DocCategory.Unclassified ? 0 : 1;
                            return (classified, classified.Category, confidence);
                        };
    }

    private readonly IMonitorBroadcaster mBroadcaster;
    private readonly Func<PageRecord,
        string,
        IngestionPersistenceMode,
        CancellationToken,
        Task<(PageRecord Page, DocCategory Category, float Confidence)>> mClassify;
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
                               CancellationTokenSource cts,
                               IngestionPersistenceMode persistMode = IngestionPersistenceMode.Full,
                               DryRunAccumulator? dryRunAcc = null)
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
                var classified = await ClassifyPageAsync(page,
                                                         job.LibraryHint,
                                                         () => progress.IncrementErrorCount(),
                                                         persistMode,
                                                         dryRunAcc,
                                                         cts.Token
                                                        );
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
    ///     Classify a single page using the LLM and upsert if the result has
    ///     non-zero confidence and a non-Unclassified category and persistence
    ///     mode is Full. <paramref name="onError" /> is invoked exactly once on
    ///     classifier failures so callers can opt into error counting without
    ///     owning a try/catch. Cancellation propagates to stop the operation.
    /// </summary>
    internal async Task<PageRecord> ClassifyPageAsync(
        PageRecord page,
        string libraryHint,
        Action? onError = null,
        IngestionPersistenceMode persistMode = IngestionPersistenceMode.Full,
        DryRunAccumulator? dryRunAcc = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        PageRecord result;
        try
        {
            (result, DocCategory category, float confidence) = await mClassify(page, libraryHint, persistMode, ct);

            long classifyMs = sw.ElapsedMilliseconds;
            dryRunAcc?.RecordClassified(result.Category, classifyMs);
            mLogger.LogInformation("Classified {Url} in {ClassifyMs}ms category={Category} confidence={Confidence:F2}",
                                   page.Url,
                                   classifyMs,
                                   category,
                                   confidence
                                  );
        }
        catch(Exception ex) when (ex is not OperationCanceledException)
        {
            long classifyMs = sw.ElapsedMilliseconds;
            mLogger.LogWarning(ex,
                               "LLM classification failed for {Url} after {ClassifyMs}ms, passing as Unclassified",
                               page.Url,
                               classifyMs);
            onError?.Invoke();
            result = page;
        }

        return result;
    }

    private async Task<(PageRecord Page, DocCategory Category, float Confidence)> ClassifyLegacyAsync(
        ILlmClassifier classifier,
        PageRecord page,
        string libraryHint,
        IngestionPersistenceMode persistMode,
        CancellationToken ct)
    {
        (DocCategory category, float confidence) = await classifier.ClassifyAsync(page, libraryHint, ct);
        PageRecord result;
        if (category != DocCategory.Unclassified && confidence > 0)
        {
            result = page with { Category = category };
            if (persistMode == IngestionPersistenceMode.Full)
                await mPageRepository.UpsertPageAsync(result, ct);
        }
        else
            result = page;
        return (result, category, confidence);
    }
}
