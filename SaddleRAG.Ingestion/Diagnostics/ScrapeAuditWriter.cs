// ScrapeAuditWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
                             TimeSpan? flushInterval = null,
                             ILogger<ScrapeAuditWriter>? logger = null,
                             int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        mLogger = logger ?? NullLogger<ScrapeAuditWriter>.Instance;
        mDefaultRepo = repo;
        mRepositoryFactory = repositoryFactory;
        mBatchSize = batchSize;
        mFlushInterval = flushInterval ?? smDefaultFlushInterval;
        mChannel = Channel.CreateBounded<PendingAuditEntry>(new BoundedChannelOptions(capacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
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
    private readonly ILogger<ScrapeAuditWriter> mLogger;
    private readonly SemaphoreSlim mFlushGate = new(1, 1);
    private readonly List<PendingAuditEntry> mPending = [];
    private const int DefaultCapacity = 10000;
    private const string QueueFullMessage = "Audit buffering is full or stopping. Stop ingestion and restore MongoDB before retrying.";
    private const string RetryMessage = "Audit persistence failed; retaining the batch and retrying after recovery";
    private const string ShutdownMessage = "Audit shutdown could not persist all pending events";
    private static readonly TimeSpan smWriteTimeout = TimeSpan.FromSeconds(5);

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

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken ct = default)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(smWriteTimeout);
        await mFlushGate.WaitAsync(bounded.Token);
        try
        {
            bool finished = false;
            while (!finished)
            {
                while (mPending.Count < mBatchSize && mChannel.Reader.TryRead(out var entry))
                    mPending.Add(entry);
                finished = mPending.Count == 0;
                if (!finished)
                {
                    await FlushBatchAsync(mPending, bounded.Token);
                    mPending.Clear();
                }
            }
        }
        finally
        {
            mFlushGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!mDisposed)
        {
            mDisposed = true;
            mChannel.Writer.TryComplete();
            await mCts.CancelAsync();
            await mLoop;
            try
            {
                await FlushAsync();
            }
            catch (Exception exception)
            {
                mLogger.LogError(exception, ShutdownMessage);
            }
            mCts.Dispose();
            mFlushGate.Dispose();
        }
    }

    private void Enqueue(string? profile, ScrapeAuditLogEntry entry)
    {
        if (!mChannel.Writer.TryWrite(new PendingAuditEntry(profile, entry)))
        {
            mLogger.LogError(QueueFullMessage);
            throw new InvalidOperationException(QueueFullMessage);
        }
    }

    private async Task RunFlushLoopAsync()
    {
        bool failed = false;
        try
        {
            while (!mCts.IsCancellationRequested)
            {
                failed = await FlushAttemptAsync(failed);
                await Task.Delay(mFlushInterval, mCts.Token);
            }
        }
        catch (OperationCanceledException) when (mCts.IsCancellationRequested)
        {
            // Dispose performs one final bounded flush.
        }
    }

    private async Task<bool> FlushAttemptAsync(bool previousFailure)
    {
        bool failed = false;
        try
        {
            await FlushAsync(mCts.Token);
        }
        catch (OperationCanceledException) when (mCts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A repository failure can arrive after shutdown cancellation. Retain
            // the batch so DisposeAsync can make its final bounded save attempt.
            if (!previousFailure && !mCts.IsCancellationRequested)
                mLogger.LogWarning(exception, RetryMessage);
            failed = true;
        }
        return failed;
    }

    private async Task FlushBatchAsync(IReadOnlyList<PendingAuditEntry> batch, CancellationToken ct)
    {
        foreach (IGrouping<string?, PendingAuditEntry> group in batch.GroupBy(item => item.Profile,
                                                                              StringComparer.Ordinal))
        {
            IScrapeAuditRepository repository = string.IsNullOrEmpty(group.Key) || mRepositoryFactory == null
                ? mDefaultRepo : mRepositoryFactory.GetScrapeAuditRepository(group.Key);
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
