// RenameLibraryIntegrationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Options;
using MongoDB.Driver;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Tests.Database;

[Trait("Category", "Integration")]
public sealed class RenameLibraryIntegrationTests : IAsyncLifetime
{
    public RenameLibraryIntegrationTests()
    {
        var settings = Options.Create(new SaddleRagDbSettings
                                          {
                                              ConnectionString = TestConnectionString,
                                              DatabaseName = mDatabaseName
                                          });
        mContext = new SaddleRagDbContext(settings);
        mRepo = new LibraryRepository(mContext);
        mRenameData = new LibraryRenameDataRepository(mContext);
    }

    private readonly SaddleRagDbContext mContext;
    private readonly string mDatabaseName = $"saddlerag-rename-library-{Guid.NewGuid():N}";
    private readonly LibraryRepository mRepo;
    private readonly LibraryRenameDataRepository mRenameData;

    public async ValueTask InitializeAsync() =>
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await mContext.Database.Client.DropDatabaseAsync(mDatabaseName);
    }

    [Fact]
    public async Task RenameLibraryRebuildsCompositeIdSoVersionIsFoundUnderNewName()
    {
        var ct = TestContext.Current.CancellationToken;
        var oldId = $"rl-old-{Guid.NewGuid():N}";
        var newId = $"rl-new-{Guid.NewGuid():N}";

        await mRepo.UpsertLibraryAsync(new LibraryRecord
                                           {
                                               Id = oldId, Name = oldId, Hint = "h",
                                               CurrentVersion = "1.0", AllVersions = ["1.0"]
                                           }, ct);
        await mRepo.UpsertVersionAsync(MakeVersion(oldId, "1.0"), ct);
        await mContext.Pages.InsertOneAsync(MakePage(oldId, "1.0", "https://x/p1"), cancellationToken: ct);
        await mContext.VersionDiffs.InsertOneAsync(MakeDiff(oldId, "1.0", "2.0"), cancellationToken: ct);
        await mContext.ProjectProfiles.InsertOneAsync(new ProjectProfile
                                                          {
                                                              Id = $"project-{Guid.NewGuid():N}",
                                                              ProjectPath = "C:\\src\\project.csproj",
                                                              ProjectName = "project",
                                                              ScannedAt = DateTime.UtcNow,
                                                              Dependencies = new Dictionary<string, string>(),
                                                              IngestedPackages = [oldId, "other", oldId]
                                                          },
                                                      cancellationToken: ct);
        await mContext.ScrapeAuditLog.InsertOneAsync(MakeAudit(oldId, "1.0"), cancellationToken: ct);

        LibraryRenameOperationRecord operation = Operation(oldId, newId);
        await SeedModeAsync(oldId,
                            LibraryIngestionOwnershipState.Committed,
                            operation.OperationId,
                            operation.SourceOwnershipReservedAtUtc!.Value,
                            ct);
        await SeedModeAsync(newId,
                            LibraryIngestionOwnershipState.Reserved,
                            operation.OperationId,
                            operation.TargetOwnershipReservedAtUtc!.Value,
                            ct);
        await mRenameData.PrepareDirectoryDefinitionsAsync(operation, ct);
        RenameLibraryResult counts = await mRenameData.ApplyLibraryRenameAsync(operation, ct);
        RenameLibraryResult retryCounts = await mRenameData.ApplyLibraryRenameAsync(operation, ct);

        Assert.Equal(1, counts.Libraries);
        Assert.Equal(counts, retryCounts);
        Assert.NotNull(await mRepo.GetLibraryAsync(newId, ct));
        Assert.Null(await mRepo.GetLibraryAsync(oldId, ct));
        // The regression: GetVersionAsync looks up by _id "{lib}/{ver}".
        Assert.NotNull(await mRepo.GetVersionAsync(newId, "1.0", ct));
        Assert.Null(await mRepo.GetVersionAsync(oldId, "1.0", ct));
        var pages = await mContext.Pages
                                  .Find(p => p.LibraryId == newId && p.Version == "1.0")
                                  .ToListAsync(ct);
        Assert.Single(pages);
        Assert.StartsWith($"{newId}/1.0/", pages[0].Id);
        VersionDiffRecord diff = Assert.Single(await mContext.VersionDiffs
                                                             .Find(item => item.LibraryId == newId)
                                                             .ToListAsync(ct));
        Assert.Equal($"{newId}/1.0-to-2.0", diff.Id);
        ProjectProfile project = Assert.Single(await mContext.ProjectProfiles
                                                             .Find(Builders<ProjectProfile>.Filter.AnyEq(
                                                                 item => item.IngestedPackages,
                                                                 newId))
                                                             .ToListAsync(ct));
        Assert.Equal([newId, "other"], project.IngestedPackages);
        ScrapeAuditLogEntry audit = Assert.Single(await mContext.ScrapeAuditLog
                                                                .Find(item => item.LibraryId == newId)
                                                                .ToListAsync(ct));
        Assert.StartsWith($"saddlerag://library/{newId}/", audit.Url, StringComparison.Ordinal);
    }

    private static LibraryVersionRecord MakeVersion(string lib, string ver) =>
        new()
            {
                Id = $"{lib}/{ver}", LibraryId = lib, Version = ver, ScrapedAt = DateTime.UtcNow,
                PageCount = 1, ChunkCount = 0, EmbeddingProviderId = "onnx",
                EmbeddingModelName = "nomic-embed-text-v1.5", EmbeddingDimensions = 768
            };

    private static PageRecord MakePage(string lib, string ver, string url) =>
        new()
            {
                Id = $"{lib}/{ver}/{Guid.NewGuid():N}",
                LibraryId = lib, Version = ver, Url = url, Title = "t",
                Category = DocCategory.HowTo, RawContent = "c", FetchedAt = DateTime.UtcNow,
                ContentHash = "h"
            };

    private static VersionDiffRecord MakeDiff(string libraryId, string fromVersion, string toVersion) =>
        new()
            {
                Id = $"{libraryId}/{fromVersion}-to-{toVersion}",
                LibraryId = libraryId,
                FromVersion = fromVersion,
                ToVersion = toVersion,
                GeneratedAt = DateTime.UtcNow,
                AddedPages = [],
                RemovedPages = [],
                ChangedPages = [],
                UnchangedPageCount = 0
            };

    private static ScrapeAuditLogEntry MakeAudit(string libraryId, string version) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            JobId = Guid.NewGuid().ToString("N"),
            LibraryId = libraryId,
            Version = version,
            Url = $"saddlerag://library/{libraryId}/documents/manual#section-1",
            ParentUrl = $"saddlerag://library/{libraryId}/documents/manual",
            Host = "library",
            DiscoveredAt = DateTime.UtcNow,
            Status = AuditStatus.Indexed
        };

    private async Task SeedModeAsync(string libraryId,
                                     LibraryIngestionOwnershipState state,
                                     string operationId,
                                     DateTime reservedAtUtc,
                                     CancellationToken ct) =>
        await mContext.LibraryIngestionModes.InsertOneAsync(new LibraryIngestionModeRecord
                                                                 {
                                                                     Id = libraryId,
                                                                     Mode = LibraryIngestionMode.Web,
                                                                     OwnershipState = state,
                                                                     LeaseOwnerToken = "rename-test-owner",
                                                                     LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
                                                                     PendingRenameOperationId = operationId,
                                                                     ReservedAtUtc = reservedAtUtc,
                                                                     UpdatedAtUtc = DateTime.UtcNow
                                                                 },
                                                             cancellationToken: ct);

    private static LibraryRenameOperationRecord Operation(string sourceLibraryId,
                                                          string targetLibraryId) =>
        new()
            {
                Id = sourceLibraryId,
                OperationId = $"rename-{Guid.NewGuid():N}",
                Kind = LibraryRenameOperationKind.Library,
                State = LibraryRenameOperationState.Applying,
                Mode = LibraryIngestionMode.Web,
                SourceLibraryId = sourceLibraryId,
                TargetLibraryId = targetLibraryId,
                SourceOwnershipReservedAtUtc = RenameReservedAtUtc,
                TargetOwnershipReservedAtUtc = RenameReservedAtUtc,
                StartedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

    private const string TestConnectionString = "mongodb://localhost:27017";
    private static readonly DateTime RenameReservedAtUtc =
        new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
}
