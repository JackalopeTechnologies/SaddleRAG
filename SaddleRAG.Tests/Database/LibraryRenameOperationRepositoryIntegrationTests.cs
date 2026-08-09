// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.Options;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Tests.Database;

[Trait("Category", "Integration")]
public sealed class LibraryRenameOperationRepositoryIntegrationTests : IAsyncLifetime
{
    private string mDatabaseName = string.Empty;
    private SaddleRagDbContext mContext = new(Options.Create(new SaddleRagDbSettings()));
    private LibraryRenameOperationRepository mRepository = null!;

    public ValueTask InitializeAsync()
    {
        mDatabaseName = $"saddlerag-rename-operation-{Guid.NewGuid():N}";
        mContext = new SaddleRagDbContext(Options.Create(new SaddleRagDbSettings
                                                            {
                                                                ConnectionString = TestConnectionString,
                                                                DatabaseName = mDatabaseName
                                                            }));
        mRepository = new LibraryRenameOperationRepository(mContext);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() =>
        await mContext.Database.Client.DropDatabaseAsync(mDatabaseName);

    [Fact]
    public async Task ExactRetryResumesButDifferentOperationCannotReplaceSourceOwner()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        LibraryRenameOperationRecord operation = Operation(OperationId, TargetLibraryId);

        LibraryRenameOperationRecord? begun = await mRepository.TryBeginAsync(operation, ct);
        LibraryRenameOperationRecord? retry = await mRepository.TryBeginAsync(operation, ct);
        LibraryRenameOperationRecord? competing = await mRepository.TryBeginAsync(
                                                       Operation(DifferentOperationId,
                                                                 DifferentTargetLibraryId),
                                                       ct);

        Assert.Equal(operation, begun);
        Assert.Equal(operation, retry);
        Assert.Null(competing);
        Assert.False(await mRepository.TryAdvanceAsync(SourceLibraryId,
                                                       DifferentOperationId,
                                                       LibraryRenameOperationState.Applying,
                                                       LibraryRenameOperationState.MongoCommitted,
                                                       Counts,
                                                       RecordedAt.AddMinutes(1),
                                                       ct));
        Assert.True(await mRepository.TryAdvanceAsync(SourceLibraryId,
                                                      OperationId,
                                                      LibraryRenameOperationState.Applying,
                                                      LibraryRenameOperationState.MongoCommitted,
                                                      Counts,
                                                      RecordedAt.AddMinutes(1),
                                                      ct));
        Assert.False(await mRepository.TryDeleteAsync(SourceLibraryId,
                                                      OperationId,
                                                      LibraryRenameOperationState.VectorCommitted,
                                                      ct));
        Assert.True(await mRepository.TryAdvanceAsync(SourceLibraryId,
                                                      OperationId,
                                                      LibraryRenameOperationState.MongoCommitted,
                                                      LibraryRenameOperationState.VectorCommitted,
                                                      null,
                                                      RecordedAt.AddMinutes(2),
                                                      ct));
        Assert.True(await mRepository.TryDeleteAsync(SourceLibraryId,
                                                     OperationId,
                                                     LibraryRenameOperationState.VectorCommitted,
                                                     ct));
        Assert.Null(await mRepository.GetAsync(SourceLibraryId, ct));
    }

    private static LibraryRenameOperationRecord Operation(string operationId, string targetLibraryId) =>
        new()
            {
                Id = SourceLibraryId,
                OperationId = operationId,
                Kind = LibraryRenameOperationKind.Library,
                State = LibraryRenameOperationState.Applying,
                Mode = LibraryIngestionMode.Web,
                SourceLibraryId = SourceLibraryId,
                TargetLibraryId = targetLibraryId,
                SourceOwnershipReservedAtUtc = RecordedAt,
                TargetOwnershipReservedAtUtc = RecordedAt,
                StartedAtUtc = RecordedAt,
                UpdatedAtUtc = RecordedAt
            };

    private static readonly RenameLibraryResult Counts = new(Libraries: 1,
                                                              Versions: 1,
                                                              Chunks: 1,
                                                              Pages: 1,
                                                              Profiles: 0,
                                                              Indexes: 0,
                                                              Bm25Shards: 0,
                                                              ExcludedSymbols: 0,
                                                              ScrapeJobs: 0);
    private static readonly DateTime RecordedAt = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
    private const string TestConnectionString = "mongodb://localhost:27017";
    private const string SourceLibraryId = "source-library";
    private const string TargetLibraryId = "target-library";
    private const string DifferentTargetLibraryId = "other-target";
    private const string OperationId = "rename-operation";
    private const string DifferentOperationId = "different-operation";
}
