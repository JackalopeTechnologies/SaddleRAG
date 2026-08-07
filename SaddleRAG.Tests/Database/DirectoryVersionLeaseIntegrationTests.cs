// DirectoryVersionLeaseIntegrationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Tests.Database;

[Trait("Category", "Integration")]
public sealed class DirectoryVersionLeaseIntegrationTests : IAsyncLifetime
{
    public DirectoryVersionLeaseIntegrationTests()
    {
        mRepository = new LibraryRepository(mContext);
    }

    private string mDatabaseName = string.Empty;
    private SaddleRagDbContext mContext = new(Options.Create(new SaddleRagDbSettings()));
    private LibraryRepository mRepository;

    public async ValueTask InitializeAsync()
    {
        mDatabaseName = $"saddlerag-directory-lease-{Guid.NewGuid():N}";
        mContext = new SaddleRagDbContext(Options.Create(new SaddleRagDbSettings
                                                            {
                                                                ConnectionString = TestConnectionString,
                                                                DatabaseName = mDatabaseName
                                                            }));
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        mRepository = new LibraryRepository(mContext);
    }

    public async ValueTask DisposeAsync()
    {
        await mContext.Database.Client.DropDatabaseAsync(mDatabaseName);
    }

    [Fact]
    public async Task ConcurrentClaimsAllowOnlyOneScanRunToOwnSameDateVersion()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Task<DirectoryVersionClaimResult> firstTask = mRepository.TryClaimDirectoryVersionAsync(
                                                          Version("scan-first",
                                                                  VersionPublicationState.Building),
                                                          ct);
        Task<DirectoryVersionClaimResult> secondTask = mRepository.TryClaimDirectoryVersionAsync(
                                                           Version("scan-second",
                                                                   VersionPublicationState.Building),
                                                           ct);

        DirectoryVersionClaimResult[] claims = await Task.WhenAll(firstTask, secondTask);
        DirectoryVersionClaimResult acquired = Assert.Single(claims,
                                                             claim => claim.Status ==
                                                                      DirectoryVersionClaimStatus.Acquired);
        Assert.False(acquired.RequiresCleanup);
        Assert.Single(claims,
                      claim => claim.Status == DirectoryVersionClaimStatus.InProgress);
        LibraryVersionRecord? stored = await mRepository.GetVersionAsync(LibraryId, VersionName, ct);
        Assert.NotNull(stored);
        string owner = Assert.IsType<string>(stored.ScanRunId);
        string loser = owner == "scan-first" ? "scan-second" : "scan-first";

        Assert.False(await mRepository.TryBeginDirectoryVersionCleanupAsync(LibraryId,
                                                                            VersionName,
                                                                            loser,
                                                                            ct));
        Assert.False(await mRepository.TryPublishDirectoryVersionAsync(
                         Version(loser, VersionPublicationState.Published),
                         loser,
                         ct));
        Assert.True(await mRepository.TryPublishDirectoryVersionAsync(
                        Version(owner, VersionPublicationState.Published),
                        owner,
                        ct));
        DirectoryVersionClaimResult published = await mRepository.TryClaimDirectoryVersionAsync(
                                                    Version("scan-third",
                                                            VersionPublicationState.Building),
                                                    ct);
        Assert.Equal(DirectoryVersionClaimStatus.AlreadyPublished, published.Status);
    }

    [Fact]
    public async Task PriorCleanupCannotOverwriteANewerSameDateOwner()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await mRepository.UpsertVersionAsync(Version("scan-failed", VersionPublicationState.Failed), ct);
        DirectoryVersionClaimResult retry = await mRepository.TryClaimDirectoryVersionAsync(
                                                Version("scan-retry", VersionPublicationState.Building),
                                                ct);
        Assert.Equal(DirectoryVersionClaimStatus.Acquired, retry.Status);
        Assert.True(retry.RequiresCleanup);
        Assert.True(await mRepository.TryBeginDirectoryVersionCleanupAsync(LibraryId,
                                                                           VersionName,
                                                                           "scan-retry",
                                                                           ct));
        await mRepository.DeleteVersionAsync(LibraryId, VersionName, ct);

        DirectoryVersionClaimResult newer = await mRepository.TryClaimDirectoryVersionAsync(
                                                Version("scan-newer", VersionPublicationState.Building),
                                                ct);
        bool staleFailureRecorded = await mRepository.TryRecordDirectoryVersionFailureAsync(
                                        Version("scan-retry", VersionPublicationState.Failed),
                                        "scan-retry",
                                        ct);
        LibraryVersionRecord? stored = await mRepository.GetVersionAsync(LibraryId, VersionName, ct);

        Assert.Equal(DirectoryVersionClaimStatus.Acquired, newer.Status);
        Assert.False(staleFailureRecorded);
        Assert.NotNull(stored);
        Assert.Equal("scan-newer", stored.ScanRunId);
        Assert.Equal(VersionPublicationState.Building, stored.PublicationState);
    }

    private static LibraryVersionRecord Version(string scanRunId, VersionPublicationState state) => new()
        {
            Id = $"{LibraryId}/{VersionName}",
            LibraryId = LibraryId,
            Version = VersionName,
            ScrapedAt = ScrapedAt,
            PageCount = 0,
            ChunkCount = 0,
            EmbeddingProviderId = string.Empty,
            EmbeddingModelName = string.Empty,
            EmbeddingDimensions = 0,
            PublicationState = state,
            ScanRunId = scanRunId
        };

    private static readonly DateTime ScrapedAt = new(year: 2026,
                                                     month: 8,
                                                     day: 4,
                                                     hour: 18,
                                                     minute: 0,
                                                     second: 0,
                                                     DateTimeKind.Utc);
    private const string TestConnectionString = "mongodb://localhost:27017";
    private const string LibraryId = "manual-library";
    private const string VersionName = "2026-08-04";
}
