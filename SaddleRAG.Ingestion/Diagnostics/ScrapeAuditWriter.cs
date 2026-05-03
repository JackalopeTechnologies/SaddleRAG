// ScrapeAuditWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Threading.Channels;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;

#endregion

namespace SaddleRAG.Ingestion.Diagnostics;

/// <summary>
///     Buffers scrape audit events in memory and flushes them to the repository
///     either when the batch reaches <see cref="DefaultBatchSize"/> entries or
///     after <see cref="smDefaultFlushInterval"/>, whichever comes first.
///     <see cref="DisposeAsync"/> drains any remaining buffered entries.
/// </summary>
public sealed class ScrapeAuditWriter : IScrapeAuditWriter
{
    public ScrapeAuditWriter(IScrapeAuditRepository repo,
                             int batchSize = DefaultBatchSize,
                             TimeSpan? flushInterval = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        mRepo = repo;
        mBatchSize = batchSize;
        mFlushInterval = flushInterval ?? smDefaultFlushInterval;
        mChannel = Channel.CreateUnbounded<ScrapeAuditLogEntry>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
        mLoop = Task.Run(RunFlushLoopAsync);
    }

    private const int DefaultBatchSize = 500;
    private static readonly TimeSpan smDefaultFlushInterval = TimeSpan.FromSeconds(1);

    private readonly IScrapeAuditRepository mRepo;
    private readonly int mBatchSize;
    private readonly TimeSpan mFlushInterval;
    private readonly Channel<ScrapeAuditLogEntry> mChannel;
    private readonly Task mLoop;
    private readonly CancellationTokenSource mCts = new();
    private bool mDisposed;

    #region RecordSkipped method

    /// <inheritdoc/>
    public void RecordSkipped(AuditContext ctx, string url, string? parentUrl, string host, int depth,
                              AuditSkipReason reason, string? detail)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentNullException.ThrowIfNull(host);
        Enqueue(BuildEntry(ctx, url, parentUrl, host, depth, AuditStatus.Skipped, reason, detail, null));
    }

    #endregion

    #region RecordFetched method

    /// <inheritdoc/>
    public void RecordFetched(AuditContext ctx, string url, string? parentUrl, string host, int depth)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentNullException.ThrowIfNull(host);
        Enqueue(BuildEntry(ctx, url, parentUrl, host, depth, AuditStatus.Fetched, null, null, null));
    }

    #endregion

    #region RecordFailed method

    /// <inheritdoc/>
    public void RecordFailed(AuditContext ctx, string url, string? parentUrl, string host, int depth, string error)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrEmpty(error);
        Enqueue(BuildEntry(ctx, url, parentUrl, host, depth, AuditStatus.Failed, null, null,
                           new AuditPageOutcome { Error = error }));
    }

    #endregion

    #region RecordIndexed method

    /// <inheritdoc/>
    public void RecordIndexed(AuditContext ctx, string url, string? parentUrl, string host, int depth,
                              AuditPageOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(outcome);
        Enqueue(BuildEntry(ctx, url, parentUrl, host, depth, AuditStatus.Indexed, null, null, outcome));
    }

    #endregion

    #region FlushAsync method

    /// <inheritdoc/>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        var batch = new List<ScrapeAuditLogEntry>(mBatchSize);
        while (mChannel.Reader.TryRead(out var entry))
            batch.Add(entry);

        var flushTask = batch.Count > 0
            ? mRepo.InsertManyAsync(batch, ct)
            : Task.CompletedTask;
        await flushTask;
    }

    #endregion

    #region DisposeAsync method

    /// <inheritdoc/>
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
            catch (OperationCanceledException)
            {
            }
            await FlushAsync();
            mCts.Dispose();
        }
    }

    #endregion

    private void Enqueue(ScrapeAuditLogEntry entry) => mChannel.Writer.TryWrite(entry);

    private async Task RunFlushLoopAsync()
    {
        var buffer = new List<ScrapeAuditLogEntry>(mBatchSize);
        while (!mCts.IsCancellationRequested)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(mCts.Token);
                timeoutCts.CancelAfter(mFlushInterval);
                while (await mChannel.Reader.WaitToReadAsync(timeoutCts.Token))
                    await DrainAvailableAsync(buffer);
            }
            catch (OperationCanceledException)
            {
            }

            var periodicFlushTask = buffer.Count > 0
                ? mRepo.InsertManyAsync(buffer, CancellationToken.None)
                : Task.CompletedTask;
            await periodicFlushTask;
            buffer.Clear();
        }

        var finalFlushTask = buffer.Count > 0
            ? mRepo.InsertManyAsync(buffer, CancellationToken.None)
            : Task.CompletedTask;
        await finalFlushTask;
    }

    private async Task DrainAvailableAsync(List<ScrapeAuditLogEntry> buffer)
    {
        while (mChannel.Reader.TryRead(out var entry))
        {
            buffer.Add(entry);
            if (buffer.Count >= mBatchSize)
            {
                await mRepo.InsertManyAsync(buffer, mCts.Token);
                buffer.Clear();
            }
        }
    }

    private static ScrapeAuditLogEntry BuildEntry(AuditContext ctx, string url, string? parentUrl,
                                                   string host, int depth, AuditStatus status,
                                                   AuditSkipReason? reason, string? detail,
                                                   AuditPageOutcome? outcome) => new()
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
}
