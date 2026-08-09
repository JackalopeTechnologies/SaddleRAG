// LibraryIngestionModeLeaseManager.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Creates auto-renewed, exact-token leases over durable library source-mode ownership.</summary>
public sealed class LibraryIngestionModeLeaseManager : ILibraryIngestionModeLeaseManager
{
    public LibraryIngestionModeLeaseManager(RepositoryFactory repositoryFactory, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        mRepositoryFactory = repositoryFactory;
        mTimeProvider = timeProvider;
    }

    private readonly RepositoryFactory mRepositoryFactory;
    private readonly TimeProvider mTimeProvider;

    public async Task<ILibraryIngestionModeLease?> TryAcquireAsync(string? profile,
                                                                   string libraryId,
                                                                   LibraryIngestionMode mode,
                                                                   CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ILibraryIngestionModeRepository repository =
            mRepositoryFactory.GetLibraryIngestionModeRepository(profile);
        string ownerToken = Guid.NewGuid().ToString("N");
        DateTime nowUtc = mTimeProvider.GetUtcNow().UtcDateTime;
        LibraryIngestionModeRecord? record = await repository.TryAcquireAsync(libraryId,
                                                                               mode,
                                                                               ownerToken,
                                                                               nowUtc,
                                                                               nowUtc + smLeaseDuration,
                                                                               ct);
        ILibraryIngestionModeLease? result = record == null
                                                ? null
                                                : new LibraryIngestionModeLease(repository,
                                                                                record,
                                                                                ownerToken,
                                                                                mTimeProvider);
        return result;
    }

    public async Task<ILibraryIngestionModeLease?> TryAcquireRenameRecoveryAsync(
        string? profile,
        string libraryId,
        LibraryIngestionMode mode,
        string renameOperationId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(renameOperationId);
        ILibraryIngestionModeRepository repository =
            mRepositoryFactory.GetLibraryIngestionModeRepository(profile);
        string ownerToken = Guid.NewGuid().ToString("N");
        DateTime nowUtc = mTimeProvider.GetUtcNow().UtcDateTime;
        LibraryIngestionModeRecord? record = await repository.TryAcquireRenameRecoveryAsync(
                                                   libraryId,
                                                   mode,
                                                   renameOperationId,
                                                   ownerToken,
                                                   nowUtc,
                                                   nowUtc + smLeaseDuration,
                                                   ct);
        ILibraryIngestionModeLease? result = record == null
                                                ? null
                                                : new LibraryIngestionModeLease(repository,
                                                                                record,
                                                                                ownerToken,
                                                                                mTimeProvider);
        return result;
    }

    private static readonly TimeSpan smLeaseDuration = TimeSpan.FromSeconds(30);

    private sealed class LibraryIngestionModeLease : ILibraryIngestionModeLease
    {
        internal LibraryIngestionModeLease(ILibraryIngestionModeRepository repository,
                                           LibraryIngestionModeRecord record,
                                           string ownerToken,
                                           TimeProvider timeProvider)
        {
            mRepository = repository;
            mOwnerToken = ownerToken;
            mTimeProvider = timeProvider;
            LibraryId = record.Id;
            Mode = record.Mode;
            OwnershipStateAtAcquisition = record.OwnershipState;
            mRenewalTask = RenewUntilDisposedAsync();
        }

        private readonly CancellationTokenSource mDispose = new();
        private readonly CancellationTokenSource mOwnershipLost = new();
        private readonly string mOwnerToken;
        private readonly ILibraryIngestionModeRepository mRepository;
        private readonly SemaphoreSlim mRepositoryGate = new(initialCount: 1, maxCount: 1);
        private readonly Task mRenewalTask;
        private readonly TimeProvider mTimeProvider;
        private int mDisposed;
        private int mOwnershipDeleted;

        public string LibraryId { get; }

        public LibraryIngestionMode Mode { get; private set; }

        public LibraryIngestionOwnershipState OwnershipStateAtAcquisition { get; }

        public CancellationToken OwnershipLostToken => mOwnershipLost.Token;

        public async ValueTask<bool> TryRenewAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await mRepositoryGate.WaitAsync(ct);
            bool renewed;
            try
            {
                DateTime nowUtc = mTimeProvider.GetUtcNow().UtcDateTime;
                renewed = await mRepository.TryRenewAsync(LibraryId,
                                                          Mode,
                                                          mOwnerToken,
                                                          nowUtc,
                                                          nowUtc + smLeaseDuration,
                                                          ct);
                if (!renewed)
                    SignalOwnershipLost();
            }
            finally
            {
                mRepositoryGate.Release();
            }
            return renewed;
        }

        public async ValueTask<bool> TryCommitAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await mRepositoryGate.WaitAsync(ct);
            bool committed;
            try
            {
                DateTime nowUtc = mTimeProvider.GetUtcNow().UtcDateTime;
                committed = await mRepository.TryCommitAsync(LibraryId, Mode, mOwnerToken, nowUtc, ct);
                if (!committed)
                    SignalOwnershipLost();
            }
            finally
            {
                mRepositoryGate.Release();
            }
            return committed;
        }

        public async ValueTask<bool> TryReconcileReservedModeAsync(LibraryIngestionMode detectedMode,
                                                                   CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await mRepositoryGate.WaitAsync(ct);
            bool reconciled;
            try
            {
                LibraryIngestionMode expectedMode = Mode;
                DateTime nowUtc = mTimeProvider.GetUtcNow().UtcDateTime;
                reconciled = await mRepository.TryReconcileReservedModeAsync(LibraryId,
                                                                             expectedMode,
                                                                             detectedMode,
                                                                             mOwnerToken,
                                                                             nowUtc,
                                                                             ct);
                if (reconciled)
                    Mode = detectedMode;
                else
                    SignalOwnershipLost();
            }
            finally
            {
                mRepositoryGate.Release();
            }
            return reconciled;
        }

        public async ValueTask<bool> TryAbandonReservationAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await mRepositoryGate.WaitAsync(ct);
            bool abandoned;
            try
            {
                abandoned = await mRepository.TryAbandonReservationAsync(LibraryId,
                                                                         Mode,
                                                                         mOwnerToken,
                                                                         ct);
                if (abandoned)
                    MarkOwnershipDeleted();
            }
            finally
            {
                mRepositoryGate.Release();
            }
            return abandoned;
        }

        public async ValueTask<bool> TryDeleteOwnershipAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await mRepositoryGate.WaitAsync(ct);
            bool deleted;
            try
            {
                deleted = await mRepository.TryDeleteOwnershipAsync(LibraryId, Mode, mOwnerToken, ct);
                if (deleted)
                    MarkOwnershipDeleted();
                else
                    SignalOwnershipLost();
            }
            finally
            {
                mRepositoryGate.Release();
            }
            return deleted;
        }

        public async ValueTask<bool> TryMarkPendingRenameAsync(string renameOperationId,
                                                               CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(renameOperationId);
            ThrowIfDisposed();
            await mRepositoryGate.WaitAsync(ct);
            bool marked;
            try
            {
                DateTime nowUtc = mTimeProvider.GetUtcNow().UtcDateTime;
                marked = await mRepository.TryMarkPendingRenameAsync(LibraryId,
                                                                     Mode,
                                                                     mOwnerToken,
                                                                     renameOperationId,
                                                                     nowUtc,
                                                                     ct);
                if (!marked)
                    SignalOwnershipLost();
            }
            finally
            {
                mRepositoryGate.Release();
            }
            return marked;
        }

        public async ValueTask<bool> TryClearPendingRenameAsync(string renameOperationId,
                                                                CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(renameOperationId);
            ThrowIfDisposed();
            await mRepositoryGate.WaitAsync(ct);
            bool cleared;
            try
            {
                DateTime nowUtc = mTimeProvider.GetUtcNow().UtcDateTime;
                cleared = await mRepository.TryClearPendingRenameAsync(LibraryId,
                                                                       Mode,
                                                                       mOwnerToken,
                                                                       renameOperationId,
                                                                       nowUtc,
                                                                       ct);
                if (!cleared)
                    SignalOwnershipLost();
            }
            finally
            {
                mRepositoryGate.Release();
            }
            return cleared;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref mDisposed, value: 1) == 0)
            {
                mDispose.Cancel();
                try
                {
                    await mRenewalTask;
                }
                catch(OperationCanceledException) when(mDispose.IsCancellationRequested)
                {
                }

                if (Volatile.Read(ref mOwnershipDeleted) == 0)
                {
                    await mRepositoryGate.WaitAsync(CancellationToken.None);
                    try
                    {
                        DateTime nowUtc = mTimeProvider.GetUtcNow().UtcDateTime;
                        await mRepository.TryReleaseAsync(LibraryId,
                                                          Mode,
                                                          mOwnerToken,
                                                          nowUtc,
                                                          CancellationToken.None);
                    }
                    finally
                    {
                        mRepositoryGate.Release();
                    }
                }

                mRepositoryGate.Dispose();
                mOwnershipLost.Dispose();
                mDispose.Dispose();
            }
        }

        private async Task RenewUntilDisposedAsync()
        {
            try
            {
                while(!mDispose.IsCancellationRequested)
                {
                    await Task.Delay(smRenewalInterval, mTimeProvider, mDispose.Token);
                    bool renewed = await TryRenewAsync(mDispose.Token);
                    if (!renewed)
                        break;
                }
            }
            catch(OperationCanceledException) when(mDispose.IsCancellationRequested)
            {
            }
            catch(Exception)
            {
                SignalOwnershipLost();
            }
        }

        private void MarkOwnershipDeleted()
        {
            Interlocked.Exchange(ref mOwnershipDeleted, value: 1);
            SignalOwnershipLost();
            mDispose.Cancel();
        }

        private void SignalOwnershipLost()
        {
            if (!mOwnershipLost.IsCancellationRequested)
                mOwnershipLost.Cancel();
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref mDisposed) != 0, this);
        }

        private static readonly TimeSpan smRenewalInterval = TimeSpan.FromSeconds(10);
    }
}
