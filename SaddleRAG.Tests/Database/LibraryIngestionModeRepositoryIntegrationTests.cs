// LibraryIngestionModeRepositoryIntegrationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Tests.Database;

[Trait("Category", "Integration")]
public sealed class LibraryIngestionModeRepositoryIntegrationTests : IAsyncLifetime
{
    private string mDefaultDatabaseName = string.Empty;
    private string mFirstProfileDatabaseName = string.Empty;
    private string mSecondProfileDatabaseName = string.Empty;
    private SaddleRagDbContext mDefaultContext = new(Options.Create(new SaddleRagDbSettings()));
    private RepositoryFactory mFactory = null!;
    private LibraryIngestionModeRepository mRepository = null!;

    public async ValueTask InitializeAsync()
    {
        mDefaultDatabaseName = $"saddlerag-mode-default-{Guid.NewGuid():N}";
        mFirstProfileDatabaseName = $"saddlerag-mode-first-{Guid.NewGuid():N}";
        mSecondProfileDatabaseName = $"saddlerag-mode-second-{Guid.NewGuid():N}";
        var settings = new SaddleRagDbSettings
                           {
                               ConnectionString = TestConnectionString,
                               DatabaseName = mDefaultDatabaseName,
                               Profiles = new Dictionary<string, MongoDbProfile>
                                              {
                                                  [FirstProfile] = new()
                                                                       {
                                                                           ConnectionString = TestConnectionString,
                                                                           DatabaseName = mFirstProfileDatabaseName
                                                                       },
                                                  [SecondProfile] = new()
                                                                        {
                                                                            ConnectionString = TestConnectionString,
                                                                            DatabaseName = mSecondProfileDatabaseName
                                                                        }
                                              }
                           };
        var contextFactory = new SaddleRagDbContextFactory(Options.Create(settings));
        mFactory = new RepositoryFactory(contextFactory);
        mDefaultContext = contextFactory.GetDefault();
        await mDefaultContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        await contextFactory.GetForProfile(FirstProfile).EnsureIndexesAsync(TestContext.Current.CancellationToken);
        await contextFactory.GetForProfile(SecondProfile).EnsureIndexesAsync(TestContext.Current.CancellationToken);
        mRepository = new LibraryIngestionModeRepository(mDefaultContext);
    }

    public async ValueTask DisposeAsync()
    {
        await mDefaultContext.Database.Client.DropDatabaseAsync(mDefaultDatabaseName);
        await mDefaultContext.Database.Client.DropDatabaseAsync(mFirstProfileDatabaseName);
        await mDefaultContext.Database.Client.DropDatabaseAsync(mSecondProfileDatabaseName);
    }

    [Fact]
    public async Task ConcurrentOppositeModeReservationsHaveExactlyOneWinner()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DateTime nowUtc = DateTime.UtcNow;
        Task<LibraryIngestionModeRecord?> web = mRepository.TryAcquireAsync(LibraryId,
                                                                            LibraryIngestionMode.Web,
                                                                            "web-owner",
                                                                            nowUtc,
                                                                            nowUtc.AddMinutes(1),
                                                                            ct);
        Task<LibraryIngestionModeRecord?> directory = mRepository.TryAcquireAsync(LibraryId,
                                                                                  LibraryIngestionMode.Directory,
                                                                                  "directory-owner",
                                                                                  nowUtc,
                                                                                  nowUtc.AddMinutes(1),
                                                                                  ct);

        LibraryIngestionModeRecord?[] results = await Task.WhenAll(web, directory);

        LibraryIngestionModeRecord winner = Assert.Single(results, result => result != null)!;
        Assert.Single(results, result => result == null);
        LibraryIngestionModeRecord stored = Assert.IsType<LibraryIngestionModeRecord>(
            await mRepository.GetAsync(LibraryId, ct));
        Assert.Equal(winner.Mode, stored.Mode);
        Assert.Equal(winner.LeaseOwnerToken, stored.LeaseOwnerToken);
    }

    [Fact]
    public async Task ExpiryAllowsSameModeRecoveryButStaleOwnerCannotReleaseOrFlipMode()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DateTime startUtc = DateTime.UtcNow;
        LibraryIngestionModeRecord first = Assert.IsType<LibraryIngestionModeRecord>(
            await mRepository.TryAcquireAsync(LibraryId,
                                              LibraryIngestionMode.Directory,
                                              "first-owner",
                                              startUtc,
                                              startUtc.AddSeconds(1),
                                              ct));
        DateTime recoveryUtc = startUtc.AddSeconds(2);
        LibraryIngestionModeRecord second = Assert.IsType<LibraryIngestionModeRecord>(
            await mRepository.TryAcquireAsync(LibraryId,
                                              LibraryIngestionMode.Directory,
                                              "second-owner",
                                              recoveryUtc,
                                              recoveryUtc.AddMinutes(1),
                                              ct));

        bool staleReleased = await mRepository.TryReleaseAsync(LibraryId,
                                                                LibraryIngestionMode.Directory,
                                                                first.LeaseOwnerToken!,
                                                                recoveryUtc,
                                                                ct);
        LibraryIngestionModeRecord? opposite = await mRepository.TryAcquireAsync(LibraryId,
                                                                                  LibraryIngestionMode.Web,
                                                                                  "web-owner",
                                                                                  recoveryUtc.AddMinutes(2),
                                                                                  recoveryUtc.AddMinutes(3),
                                                                                  ct);

        Assert.False(staleReleased);
        Assert.Null(opposite);
        Assert.Equal("second-owner", second.LeaseOwnerToken);
        LibraryIngestionModeRecord stored = Assert.IsType<LibraryIngestionModeRecord>(
            await mRepository.GetAsync(LibraryId, ct));
        Assert.Equal("second-owner", stored.LeaseOwnerToken);
        Assert.Equal(LibraryIngestionMode.Directory, stored.Mode);
    }

    [Fact]
    public async Task PendingRenameBlocksNormalWorkAndOnlyExactOperationCanRecover()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DateTime startUtc = DateTime.UtcNow;
        LibraryIngestionModeRecord acquired = Assert.IsType<LibraryIngestionModeRecord>(
            await mRepository.TryAcquireAsync(LibraryId,
                                              LibraryIngestionMode.Directory,
                                              "rename-owner",
                                              startUtc,
                                              startUtc.AddSeconds(1),
                                              ct));
        Assert.True(await mRepository.TryMarkPendingRenameAsync(LibraryId,
                                                                 LibraryIngestionMode.Directory,
                                                                 acquired.LeaseOwnerToken!,
                                                                 RenameOperationId,
                                                                 startUtc,
                                                                 ct));
        await mDefaultContext.LibraryRenameOperations.InsertOneAsync(
            RenameOperation(LibraryId,
                            TargetLibraryId,
                            LibraryRenameOperationState.Applying,
                            startUtc),
            cancellationToken: ct);
        DateTime recoveryUtc = startUtc.AddSeconds(2);

        LibraryIngestionModeRecord? normal = await mRepository.TryAcquireAsync(LibraryId,
                                                                               LibraryIngestionMode.Directory,
                                                                               "normal-owner",
                                                                               recoveryUtc,
                                                                               recoveryUtc.AddMinutes(1),
                                                                               ct);
        LibraryIngestionModeRecord? wrongRecovery = await mRepository.TryAcquireRenameRecoveryAsync(
                                                          LibraryId,
                                                          LibraryIngestionMode.Directory,
                                                          "wrong-operation",
                                                          "wrong-owner",
                                                          recoveryUtc,
                                                          recoveryUtc.AddMinutes(1),
                                                          ct);
        LibraryIngestionModeRecord exactRecovery = Assert.IsType<LibraryIngestionModeRecord>(
            await mRepository.TryAcquireRenameRecoveryAsync(LibraryId,
                                                            LibraryIngestionMode.Directory,
                                                            RenameOperationId,
                                                            "recovery-owner",
                                                            recoveryUtc,
                                                            recoveryUtc.AddMinutes(1),
                                                            ct));

        Assert.Null(normal);
        Assert.Null(wrongRecovery);
        Assert.Equal("recovery-owner", exactRecovery.LeaseOwnerToken);
        Assert.Equal(RenameOperationId, exactRecovery.PendingRenameOperationId);
    }

    [Fact]
    public async Task ExactRecoveryCanRearmNullMarkerOnlyAtCrashRecoverableCheckpoint()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DateTime startUtc = DateTime.UtcNow;
        _ = Assert.IsType<LibraryIngestionModeRecord>(
            await mRepository.TryAcquireAsync(LibraryId,
                                              LibraryIngestionMode.Directory,
                                              "initial-owner",
                                              startUtc,
                                              startUtc.AddSeconds(1),
                                              ct));
        await mDefaultContext.LibraryRenameOperations.InsertOneAsync(
            RenameOperation(LibraryId,
                            TargetLibraryId,
                            LibraryRenameOperationState.Applying,
                            startUtc),
            cancellationToken: ct);
        DateTime recoveryUtc = startUtc.AddSeconds(2);

        LibraryIngestionModeRecord recovered = Assert.IsType<LibraryIngestionModeRecord>(
            await mRepository.TryAcquireRenameRecoveryAsync(LibraryId,
                                                            LibraryIngestionMode.Directory,
                                                            RenameOperationId,
                                                            "recovery-owner",
                                                            recoveryUtc,
                                                            recoveryUtc.AddMinutes(1),
                                                            ct));

        Assert.Null(recovered.PendingRenameOperationId);
        Assert.Equal("recovery-owner", recovered.LeaseOwnerToken);

        const string mongoCommittedLibraryId = "mongo-committed-library";
        _ = Assert.IsType<LibraryIngestionModeRecord>(
            await mRepository.TryAcquireAsync(mongoCommittedLibraryId,
                                              LibraryIngestionMode.Directory,
                                              "mongo-owner",
                                              startUtc,
                                              startUtc.AddSeconds(1),
                                              ct));
        await mDefaultContext.LibraryRenameOperations.InsertOneAsync(
            RenameOperation(mongoCommittedLibraryId,
                            "mongo-committed-target",
                            LibraryRenameOperationState.MongoCommitted,
                            startUtc),
            cancellationToken: ct);

        LibraryIngestionModeRecord? invalidCheckpoint =
            await mRepository.TryAcquireRenameRecoveryAsync(mongoCommittedLibraryId,
                                                             LibraryIngestionMode.Directory,
                                                             RenameOperationId,
                                                             "invalid-recovery-owner",
                                                             recoveryUtc,
                                                             recoveryUtc.AddMinutes(1),
                                                             ct);

        Assert.Null(invalidCheckpoint);
    }

    [Fact]
    public async Task DurableRenameOperationBlocksSourceAndTargetOwnershipAcquisition()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DateTime nowUtc = DateTime.UtcNow;
        await mDefaultContext.LibraryRenameOperations.InsertOneAsync(
            RenameOperation(LibraryId,
                            TargetLibraryId,
                            LibraryRenameOperationState.VectorCommitted,
                            nowUtc),
            cancellationToken: ct);

        LibraryIngestionModeRecord? acquired = await mRepository.TryAcquireAsync(
                                                           LibraryId,
                                                           LibraryIngestionMode.Directory,
                                                           "new-owner",
                                                           nowUtc,
                                                           nowUtc.AddMinutes(1),
                                                           ct);
        LibraryIngestionModeRecord? targetAcquired = await mRepository.TryAcquireAsync(
                                                                 TargetLibraryId,
                                                                 LibraryIngestionMode.Directory,
                                                                 "target-owner",
                                                                 nowUtc,
                                                                 nowUtc.AddMinutes(1),
                                                                 ct);

        Assert.Null(acquired);
        Assert.Null(targetAcquired);
        Assert.Null(await mRepository.GetAsync(LibraryId, ct));
        Assert.Null(await mRepository.GetAsync(TargetLibraryId, ct));
    }

    [Fact]
    public async Task ProfilesCanOwnTheSameLibraryIdIndependently()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DateTime nowUtc = DateTime.UtcNow;
        ILibraryIngestionModeRepository first = mFactory.GetLibraryIngestionModeRepository(FirstProfile);
        ILibraryIngestionModeRepository second = mFactory.GetLibraryIngestionModeRepository(SecondProfile);

        LibraryIngestionModeRecord? firstResult = await first.TryAcquireAsync(LibraryId,
                                                                              LibraryIngestionMode.Web,
                                                                              "first-profile-owner",
                                                                              nowUtc,
                                                                              nowUtc.AddMinutes(1),
                                                                              ct);
        LibraryIngestionModeRecord? secondResult = await second.TryAcquireAsync(LibraryId,
                                                                                LibraryIngestionMode.Directory,
                                                                                "second-profile-owner",
                                                                                nowUtc,
                                                                                nowUtc.AddMinutes(1),
                                                                                ct);

        Assert.NotNull(firstResult);
        Assert.NotNull(secondResult);
        Assert.Equal(LibraryIngestionMode.Web, (await first.GetAsync(LibraryId, ct))!.Mode);
        Assert.Equal(LibraryIngestionMode.Directory, (await second.GetAsync(LibraryId, ct))!.Mode);
        Assert.Null(await mRepository.GetAsync(LibraryId, ct));
    }

    [Fact]
    public async Task ChildOnlyPartialStateIsReportedAsDurableData()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await mDefaultContext.Pages.InsertOneAsync(new PageRecord
                                                       {
                                                           Id = "partial/v1/page",
                                                           LibraryId = LibraryId,
                                                           Version = "v1",
                                                           Url = "https://partial.test/",
                                                           Title = "Partial",
                                                           Category = DocCategory.Unclassified,
                                                           RawContent = "partial",
                                                           ContentHash = "hash",
                                                           FetchedAt = DateTime.UtcNow
                                                       },
                                                   cancellationToken: ct);

        LibraryIngestionDataEvidence evidence = await mRepository.GetLibraryDataEvidenceAsync(LibraryId, ct);

        Assert.False(evidence.HasLibraryRecord);
        Assert.True(evidence.HasChildContentData);
        Assert.True(await mRepository.HasAnyLibraryDataAsync(LibraryId, ct));
    }

    private const string TestConnectionString = "mongodb://localhost:27017";
    private const string LibraryId = "shared-library";
    private const string FirstProfile = "first";
    private const string SecondProfile = "second";
    private const string RenameOperationId = "rename-operation";
    private const string TargetLibraryId = "renamed-library";

    private static LibraryRenameOperationRecord RenameOperation(
        string sourceLibraryId,
        string targetLibraryId,
        LibraryRenameOperationState state,
        DateTime timestampUtc) =>
        new()
            {
                Id = sourceLibraryId,
                OperationId = RenameOperationId,
                Kind = LibraryRenameOperationKind.Library,
                State = state,
                Mode = LibraryIngestionMode.Directory,
                SourceLibraryId = sourceLibraryId,
                TargetLibraryId = targetLibraryId,
                StartedAtUtc = timestampUtc,
                UpdatedAtUtc = timestampUtc
            };
}
