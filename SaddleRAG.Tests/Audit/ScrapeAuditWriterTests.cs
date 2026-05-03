// ScrapeAuditWriterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.DependencyInjection;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Ingestion.Diagnostics;

#endregion

namespace SaddleRAG.Tests.Audit;

public sealed class ScrapeAuditWriterTests
{
    [Fact]
    public async Task FlushesWhenBufferReachesSizeThreshold()
    {
        var spy = new SpyRepository();
        var writer = new ScrapeAuditWriter(spy, batchSize: 5,
                                           flushInterval: TimeSpan.FromMinutes(10));

        for (var i = 0; i < 5; i++)
            writer.RecordSkipped(NewCtx("job-1"), $"https://a.com/{i}",
                                 parentUrl: null, host: "a.com", depth: 1,
                                 AuditSkipReason.PatternExclude, detail: null);

        await writer.DisposeAsync();

        Assert.Equal(5, spy.Inserted.Count);
        Assert.All(spy.Inserted, e => Assert.Equal(AuditStatus.Skipped, e.Status));
    }

    [Fact]
    public async Task FlushesPeriodicallyByTime()
    {
        var spy = new SpyRepository();
        await using var writer = new ScrapeAuditWriter(spy, batchSize: 1000,
                                                       flushInterval: TimeSpan.FromMilliseconds(150));

        writer.RecordFetched(NewCtx("job-2"), "https://x.com/", null, "x.com", 0);

        await Task.Delay(400, TestContext.Current.CancellationToken);

        Assert.Single(spy.Inserted);
        Assert.Equal(AuditStatus.Fetched, spy.Inserted[0].Status);
    }

    [Fact]
    public async Task DisposeFlushesRemainingEntries()
    {
        var spy = new SpyRepository();
        var writer = new ScrapeAuditWriter(spy, batchSize: 1000,
                                           flushInterval: TimeSpan.FromMinutes(5));
        writer.RecordIndexed(NewCtx("job-3"), "https://y.com/", null, "y.com", 0,
                             new AuditPageOutcome
                             {
                                 FetchStatus = "200 OK",
                                 Category = "Overview",
                                 ChunkCount = 3
                             });

        await writer.DisposeAsync();

        Assert.Single(spy.Inserted);
        Assert.Equal(AuditStatus.Indexed, spy.Inserted[0].Status);
        var first = spy.Inserted[0];
        Assert.NotNull(first.PageOutcome);
        Assert.Equal(3, first.PageOutcome.ChunkCount);
    }

    [Fact]
    public async Task DependencyInjectionResolvesAuditWriterAndRepository()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IScrapeAuditRepository>(_ => new SpyRepository());
        services.AddSingleton<IScrapeAuditWriter>(sp =>
            new ScrapeAuditWriter(sp.GetRequiredService<IScrapeAuditRepository>()));

        await using var sp = services.BuildServiceProvider();
        var writer = sp.GetRequiredService<IScrapeAuditWriter>();
        Assert.NotNull(writer);
    }

    [Fact]
    public async Task AcceptsEmptyHostForFileSchemeUrls()
    {
        var spy = new SpyRepository();
        var writer = new ScrapeAuditWriter(spy, batchSize: 1000,
                                           flushInterval: TimeSpan.FromMinutes(5));
        var ctx = NewCtx("job-file");
        var fileUrl = "file:///E:/3rd Party Help/Mcculw_WebHelp/Welcome.htm";

        writer.RecordSkipped(ctx, fileUrl, parentUrl: null, host: "", depth: 0,
                             AuditSkipReason.PatternExclude, detail: null);
        writer.RecordFetched(ctx, fileUrl, parentUrl: null, host: "", depth: 0);
        writer.RecordFailed(ctx, fileUrl, parentUrl: null, host: "", depth: 0, error: "test-error");
        writer.RecordIndexed(ctx, fileUrl, parentUrl: null, host: "", depth: 0,
                             new AuditPageOutcome { FetchStatus = "200 OK", Category = "Overview", ChunkCount = 1 });

        await writer.DisposeAsync();

        Assert.Equal(4, spy.Inserted.Count);
        Assert.All(spy.Inserted, e => Assert.Equal(string.Empty, e.Host));
    }

    private static AuditContext NewCtx(string jobId) => new()
    {
        JobId = jobId,
        LibraryId = "lib",
        Version = "1.0"
    };

    private sealed class SpyRepository : IScrapeAuditRepository
    {
        public List<ScrapeAuditLogEntry> Inserted { get; } = new();

        public Task InsertManyAsync(IEnumerable<ScrapeAuditLogEntry> entries, CancellationToken ct = default)
        {
            Inserted.AddRange(entries);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ScrapeAuditLogEntry>> QueryAsync(string jobId, AuditStatus? status,
                                                                   AuditSkipReason? skipReason, string? host,
                                                                   string? urlSubstring, int limit,
                                                                   CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ScrapeAuditLogEntry>>(Array.Empty<ScrapeAuditLogEntry>());

        public Task<ScrapeAuditLogEntry?> GetByUrlAsync(string jobId, string url, CancellationToken ct = default)
            => Task.FromResult<ScrapeAuditLogEntry?>(null);

        public Task<AuditSummary> SummarizeAsync(string jobId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<long> DeleteByJobIdAsync(string jobId, CancellationToken ct = default)
            => Task.FromResult(0L);

        public Task<long> DeleteByLibraryVersionAsync(string libraryId, string version,
                                                       CancellationToken ct = default)
            => Task.FromResult(0L);
    }
}
