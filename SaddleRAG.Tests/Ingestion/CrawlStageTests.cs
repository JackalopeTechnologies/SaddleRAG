// CrawlStageTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     Tests the CrawlStage's contract with the rest of the streaming
///     pipeline: forward progress callbacks; on cancellation, complete the
///     output channel silently; on any other exception, fault the channel
///     with the exception AND cancel the shared CancellationTokenSource so
///     downstream stages unwind.
/// </summary>
public sealed class CrawlStageTests
{
    private sealed record CrawlAsyncCall(
        ScrapeJob Job,
        ChannelWriter<PageRecord> Output,
        string JobId,
        IReadOnlySet<string>? ResumeUrls,
        IReadOnlyList<string>? SeedUrls,
        Action<int>? OnPageFetched,
        Action<int>? OnQueued,
        Action? OnFetchError,
        CancellationToken Ct);

    private sealed class StubPageCrawler : IPageCrawler
    {
        public Func<CrawlAsyncCall, Task>? Behavior { get; set; }
        public List<CrawlAsyncCall> Calls { get; } = [];

        public Task CrawlAsync(ScrapeJob job,
                               ChannelWriter<PageRecord> output,
                               string jobId = "",
                               IReadOnlySet<string>? resumeUrls = null,
                               IReadOnlyList<string>? seedUrls = null,
                               Action<int>? onPageFetched = null,
                               Action<int>? onQueued = null,
                               Action? onFetchError = null,
                               IngestionPersistenceMode persistMode = IngestionPersistenceMode.Full,
                               DryRunAccumulator? dryRunAcc = null,
                               CancellationToken ct = default)
        {
            var call = new CrawlAsyncCall(job,
                                          output,
                                          jobId,
                                          resumeUrls,
                                          seedUrls,
                                          onPageFetched,
                                          onQueued,
                                          onFetchError,
                                          ct
                                         );
            Calls.Add(call);
            return Behavior?.Invoke(call) ?? Task.CompletedTask;
        }

        public Task<PageRecord?> FetchSinglePageAsync(string libraryId,
                                                      string version,
                                                      string url,
                                                      CancellationToken ct = default) =>
            Task.FromResult<PageRecord?>(null);
    }

    private static ScrapeJob NewJob() => new()
                                             {
                                                 LibraryId = "lib",
                                                 Version = "v1",
                                                 RootUrl = "https://example.test/",
                                                 LibraryHint = "lib",
                                                 AllowedUrlPatterns = []
                                             };

    private static ScrapeJobRecord NewProgress(string jobId = "job-1") => new()
                                                                             {
                                                                                 Id = jobId,
                                                                                 Job = NewJob()
                                                                             };

    [Fact]
    public async Task RunAsyncPassesJobIdResumeUrlsAndSeedUrlsThroughToCrawler()
    {
        var crawler = new StubPageCrawler();
        var stage = new CrawlStage(crawler, NullLogger.Instance);
        var output = Channel.CreateBounded<PageRecord>(4);
        using var cts = new CancellationTokenSource();
        var progress = NewProgress("abc-123");
        IReadOnlySet<string> resume = new HashSet<string> { "https://example.test/a" };
        IReadOnlyList<string> seed = new List<string> { "https://example.test/seed" };

        await stage.RunAsync(NewJob(), output.Writer, resume, seed, progress, null, cts);

        var call = Assert.Single(crawler.Calls);
        Assert.Equal("abc-123", call.JobId);
        Assert.Same(resume, call.ResumeUrls);
        Assert.Same(seed, call.SeedUrls);
    }

    [Fact]
    public async Task RunAsyncOnPageFetchedCallbackUpdatesProgressAndInvokesOnProgress()
    {
        var crawler = new StubPageCrawler();
        var progressInvocations = 0;
        crawler.Behavior = call =>
        {
            call.OnPageFetched?.Invoke(7);
            return Task.CompletedTask;
        };
        var stage = new CrawlStage(crawler, NullLogger.Instance);
        var output = Channel.CreateBounded<PageRecord>(4);
        using var cts = new CancellationTokenSource();
        var progress = NewProgress();

        await stage.RunAsync(NewJob(),
                             output.Writer,
                             null,
                             null,
                             progress,
                             _ => progressInvocations++,
                             cts
                            );

        Assert.Equal(7, progress.PagesFetched);
        Assert.Equal(1, progressInvocations);
    }

    [Fact]
    public async Task RunAsyncOnQueuedCallbackUpdatesPagesQueuedAndDoesNotInvokeOnProgress()
    {
        var crawler = new StubPageCrawler
                          {
                              Behavior = call =>
                                         {
                                             call.OnQueued?.Invoke(42);
                                             return Task.CompletedTask;
                                         }
                          };
        var stage = new CrawlStage(crawler, NullLogger.Instance);
        var output = Channel.CreateBounded<PageRecord>(4);
        using var cts = new CancellationTokenSource();
        var progress = NewProgress();
        var progressInvocations = 0;

        await stage.RunAsync(NewJob(), output.Writer, null, null, progress, _ => progressInvocations++, cts);

        Assert.Equal(42, progress.PagesQueued);
        Assert.Equal(0, progressInvocations);
    }

    [Fact]
    public async Task RunAsyncOnFetchErrorCallbackIncrementsErrorCountAndFiresOnProgress()
    {
        var crawler = new StubPageCrawler
                          {
                              Behavior = call =>
                                         {
                                             call.OnFetchError?.Invoke();
                                             return Task.CompletedTask;
                                         }
                          };
        var stage = new CrawlStage(crawler, NullLogger.Instance);
        var output = Channel.CreateBounded<PageRecord>(4);
        using var cts = new CancellationTokenSource();
        var progress = NewProgress();
        var progressInvocations = 0;

        await stage.RunAsync(NewJob(), output.Writer, null, null, progress, _ => progressInvocations++, cts);

        Assert.Equal(1, progress.ErrorCount);
        Assert.Equal(1, progressInvocations);
    }

    [Fact]
    public async Task RunAsyncOnCancelCompletesChannelSilentlyAndDoesNotFaultIt()
    {
        var crawler = new StubPageCrawler
                          {
                              Behavior = call => Task.FromCanceled(new CancellationToken(canceled: true))
                          };
        var stage = new CrawlStage(crawler, NullLogger.Instance);
        var output = Channel.CreateBounded<PageRecord>(4);
        using var cts = new CancellationTokenSource();
        var progress = NewProgress();

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
                                                            stage.RunAsync(NewJob(),
                                                                           output.Writer,
                                                                           null,
                                                                           null,
                                                                           progress,
                                                                           null,
                                                                           cts
                                                                          )
                                                       );

        await output.Reader.Completion;
    }

    [Fact]
    public async Task RunAsyncOnFatalErrorFaultsChannelWithExceptionAndCancelsCts()
    {
        var crawler = new StubPageCrawler
                          {
                              Behavior = _ => throw new InvalidOperationException("boom")
                          };
        var stage = new CrawlStage(crawler, NullLogger.Instance);
        var output = Channel.CreateBounded<PageRecord>(4);
        using var cts = new CancellationTokenSource();
        var progress = NewProgress();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                             stage.RunAsync(NewJob(),
                                                                                            output.Writer,
                                                                                            null,
                                                                                            null,
                                                                                            progress,
                                                                                            null,
                                                                                            cts
                                                                                           )
                                                                        );
        Assert.Equal("boom", thrown.Message);

        Assert.True(cts.IsCancellationRequested);

        var channelEx =
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await output.Reader.Completion);
        Assert.Equal("boom", channelEx.Message);
    }
}
