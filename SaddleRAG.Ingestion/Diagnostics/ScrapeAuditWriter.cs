// ScrapeAuditWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Threading.Channels;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Ingestion.Diagnostics;

/// <summary>
///     Buffers scrape audit events in memory and flushes them to the repository
///     either when the batch reaches <see cref="DefaultBatchSize" /> entries or
///     after <see cref="smDefaultFlushInterval" />, whichever comes first.
///     <see cref="DisposeAsync" /> drains any remaining buffered entries.
/// </summary>
public sealed class ScrapeAuditWriter : IScrapeAuditWriter
{
    public ScrapeAuditWriter(IScrapeAuditRepository repo,
                             int batchSize = DefaultBatchSize,
                             TimeSpan? flushInterval = null)
        : this(repo, null, batchSize, flushInterval)
    {
    }

    public ScrapeAuditWriter(IScrapeAuditRepository repo,
                             RepositoryFactory? repositoryFactory,
                             int batchSize = DefaultBatchSize,
                             TimeSpan? flushInterval = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        mDefaultRepo = repo;
        mRepositoryFactory = repositoryFactory;
        mBatchSize = batchSize;
        mFlushInterval = flushInterval ?? smDefaultFlushInterval;
        mChannel = Channel.CreateUnbounded<PendingAuditEntry>(new UnboundedChannelOptions
                                                                  {
                                                                      SingleReader = false,
                                                                      SingleWriter = false
                                                                  });
        mLoop = Task.Run(RunFlushLoopAsync);
    }

    private readonly int mBatchSize;
    private readonly Channel<PendingAuditEntry> mChannel;
    private readonly CancellationTokenSource mCts = new CancellationTokenSource();
    private readonly TimeSpan mFlushInterval;
    private readonly Task mLoop;

    private readonly IScrapeAuditRepository mDefaultRepo;
    private readonly RepositoryFactory? mRepositoryFactory;
    private bool mDisposed;

    #region RecordSkipped method

    /// <inheritdoc />
    public void RecordSkipped(AuditContext ctx,
                              string url,
                              string? parentUrl,
                              string host,
                              int depth,
                              AuditSkipReason reason,
                              string? detail)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentNullException.ThrowIfNull(host);
        Enqueue(ctx.Profile,
                BuildEntry(ctx,
                           url,
                           parentUrl,
                           host,
                           depth,
                           AuditStatus.Skipped,
                           reason,
                           detail,
                           outcome: null
                          )
               );
    }

    #endregion

    #region RecordFetched method

    /// <inheritdoc />
    public void RecordFetched(AuditContext ctx,
                              string url,
                              string? parentUrl,
                              string host,
                              int depth)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentNullException.ThrowIfNull(host);
        Enqueue(ctx.Profile,
                BuildEntry(ctx,
                           url,
                           parentUrl,
                           host,
                           depth,
                           AuditStatus.Fetched,
                           reason: null,
                           detail: null,
                           outcome: null
                          )
               );
    }

    #endregion

    #region RecordFailed method

    /// <inheritdoc />
    public void RecordFailed(AuditContext ctx,
                             string url,
                             string? parentUrl,
                             string host,
                             int depth,
                             string error)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrEmpty(error);
        Enqueue(ctx.Profile,
                BuildEntry(ctx,
                           url,
                           parentUrl,
                           host,
                           depth,
                           AuditStatus.Failed,
                           reason: null,
                           detail: null,
                           new AuditPageOutcome { Error = error }
                          )
               );
    }

    #endregion

    #region RecordIndexed method

    /// <inheritdoc />
    public void RecordIndexed(AuditContext ctx,
                              string url,
                              string? parentUrl,
                              string host,
                              int depth,
                              AuditPageOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(outcome);
        Enqueue(ctx.Profile,
                BuildEntry(ctx,
                           url,
                           parentUrl,
                           host,
                           depth,
                           AuditStatus.Indexed,
                           reason: null,
                           detail: null,
                           outcome
                          )
               );
    }

    #endregion

    #region FlushAsync method

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken ct = default)
    {
        var batch = new List<PendingAuditEntry>(mBatchSize);
        while (mChannel.Reader.TryRead(out var entry))
            batch.Add(entry);

        await FlushBatchAsync(batch, ct);
    }

    #endregion

    #region DisposeAsync method

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!mDisposed)
        {
            mDisposed = true;
            mChannel.Writer.TryComplete();
            await mCts.CancelAsync();
            try
            {
                await mLoop;
            }
            catch(OperationCanceledException)
            {
            }

            await FlushAsync();
            mCts.Dispose();
        }
    }

    #endregion

    private void Enqueue(string? profile, ScrapeAuditLogEntry entry) =>
        mChannel.Writer.TryWrite(new PendingAuditEntry(profile, entry));

    private async Task RunFlushLoopAsync()
    {
        var buffer = new List<PendingAuditEntry>(mBatchSize);
        while (!mCts.IsCancellationRequested)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(mCts.Token);
                timeoutCts.CancelAfter(mFlushInterval);
                while (await mChannel.Reader.WaitToReadAsync(timeoutCts.Token))
                    await DrainAvailableAsync(buffer);
            }
            catch(OperationCanceledException)
            {
            }

            await FlushBatchAsync(buffer, CancellationToken.None);
            buffer.Clear();
        }

        await FlushBatchAsync(buffer, CancellationToken.None);
    }

    private async Task DrainAvailableAsync(List<PendingAuditEntry> buffer)
    {
        while (mChannel.Reader.TryRead(out var entry))
        {
            buffer.Add(entry);
            if (buffer.Count >= mBatchSize)
            {
                await FlushBatchAsync(buffer, mCts.Token);
                buffer.Clear();
            }
        }
    }

    private async Task FlushBatchAsync(IReadOnlyList<PendingAuditEntry> batch, CancellationToken ct)
    {
        foreach(IGrouping<string?, PendingAuditEntry> group in batch.GroupBy(item => item.Profile,
                                                                              StringComparer.Ordinal))
        {
            IScrapeAuditRepository repository = string.IsNullOrEmpty(group.Key) || mRepositoryFactory == null
                                                    ? mDefaultRepo
                                                    : mRepositoryFactory.GetScrapeAuditRepository(group.Key);
            await repository.InsertManyAsync(group.Select(item => item.Entry), ct);
        }
    }

    private static ScrapeAuditLogEntry BuildEntry(AuditContext ctx,
                                                  string url,
                                                  string? parentUrl,
                                                  string host,
                                                  int depth,
                                                  AuditStatus status,
                                                  AuditSkipReason? reason,
                                                  string? detail,
                                                  AuditPageOutcome? outcome) =>
        new ScrapeAuditLogEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                JobId = ctx.JobId,
                LibraryId = ctx.LibraryId,
                Version = ctx.Version,
                Url = url,
                ParentUrl = parentUrl,
                Host = host,
                Depth = depth,
                DiscoveredAt = DateTime.UtcNow,
                Status = status,
                SkipReason = reason,
                SkipDetail = detail,
                PageOutcome = outcome
            };

    private const int DefaultBatchSize = 500;
    private static readonly TimeSpan smDefaultFlushInterval = TimeSpan.FromSeconds(seconds: 1);

    private sealed record PendingAuditEntry(string? Profile, ScrapeAuditLogEntry Entry);
}
