// RenameVersionIntegrationTests.cs
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
public sealed class RenameVersionIntegrationTests : IAsyncLifetime
{
    public RenameVersionIntegrationTests()
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
    private readonly string mDatabaseName = $"saddlerag-rename-version-{Guid.NewGuid():N}";
    private readonly LibraryRepository mRepo;
    private readonly LibraryRenameDataRepository mRenameData;

    public async ValueTask InitializeAsync() =>
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await mContext.Database.Client.DropDatabaseAsync(mDatabaseName);
    }

    [Fact]
    public async Task RenameVersionMovesDataRepointsCurrentAndRebuildsIds()
    {
        var ct = TestContext.Current.CancellationToken;
        var lib = $"rv-{Guid.NewGuid():N}";
        await mRepo.UpsertLibraryAsync(new LibraryRecord
                                           {
                                               Id = lib, Name = lib, Hint = "h",
                                               CurrentVersion = "current", AllVersions = ["current"]
                                           }, ct);
        await mRepo.UpsertVersionAsync(MakeVersion(lib, "current"), ct);
        await mRepo.UpsertVersionAsync(MakeVersion(lib, "next", "current"), ct);
        await mContext.Pages.InsertOneAsync(MakePage(lib, "current", "https://x/p1"), cancellationToken: ct);
        await mContext.VersionDiffs.InsertOneAsync(MakeDiff(lib, "current", "next"), cancellationToken: ct);
        await mContext.ScrapeAuditLog.InsertOneAsync(MakeAudit(lib, "current"), cancellationToken: ct);

        RenameLibraryResult counts = await ApplyVersionRenameAsync(lib, "current", "v8", ct);

        Assert.Equal(1, counts.Versions);
        Assert.NotNull(await mRepo.GetVersionAsync(lib, "v8", ct));
        Assert.Null(await mRepo.GetVersionAsync(lib, "current", ct));
        var libRec = await mRepo.GetLibraryAsync(lib, ct);
        Assert.NotNull(libRec);
        Assert.Equal("v8", libRec.CurrentVersion);
        Assert.Contains("v8", libRec.AllVersions);
        Assert.DoesNotContain("current", libRec.AllVersions);
        var pages = await mContext.Pages.Find(p => p.LibraryId == lib && p.Version == "v8").ToListAsync(ct);
        Assert.Single(pages);
        Assert.StartsWith($"{lib}/v8/", pages[0].Id);
        LibraryVersionRecord dependent = Assert.IsType<LibraryVersionRecord>(
            await mRepo.GetVersionAsync(lib, "next", ct));
        Assert.Equal("v8", dependent.PreviousVersion);
        VersionDiffRecord diff = Assert.Single(await mContext.VersionDiffs
                                                             .Find(item => item.LibraryId == lib)
                                                             .ToListAsync(ct));
        Assert.Equal("v8", diff.FromVersion);
        Assert.Equal($"{lib}/v8-to-next", diff.Id);
        ScrapeAuditLogEntry audit = Assert.Single(await mContext.ScrapeAuditLog
                                                                .Find(item => item.LibraryId == lib)
                                                                .ToListAsync(ct));
        Assert.Equal("v8", audit.Version);
    }

    [Fact]
    public async Task RenameVersionReturnsCollisionWhenTargetExists()
    {
        var ct = TestContext.Current.CancellationToken;
        var lib = $"rv-col-{Guid.NewGuid():N}";
        await mRepo.UpsertLibraryAsync(new LibraryRecord
                                           { Id = lib, Name = lib, Hint = "h",
                                             CurrentVersion = "v9", AllVersions = ["v8", "v9"] }, ct);
        await mRepo.UpsertVersionAsync(MakeVersion(lib, "v8"), ct);
        await mRepo.UpsertVersionAsync(MakeVersion(lib, "v9"), ct);

        RenameLibraryOutcome outcome = await mRenameData.PreflightVersionRenameAsync(lib,
                                                                                      "v8",
                                                                                      "v9",
                                                                                      ct);

        Assert.Equal(RenameLibraryOutcome.Collision, outcome);
        Assert.NotNull(await mRepo.GetVersionAsync(lib, "v8", ct));
    }

    [Fact]
    public async Task RenameVersionReturnsNotFoundWhenSourceMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        var lib = $"rv-nf-{Guid.NewGuid():N}";
        RenameLibraryOutcome outcome = await mRenameData.PreflightVersionRenameAsync(lib,
                                                                                      "nope",
                                                                                      "v8",
                                                                                      ct);
        Assert.Equal(RenameLibraryOutcome.NotFound, outcome);
    }

    [Fact]
    public async Task RenameVersionRejectsTargetShapedOrphanIdBeforeUpsert()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string libraryId = $"rv-orphan-{Guid.NewGuid():N}";
        await mRepo.UpsertLibraryAsync(new LibraryRecord
                                           {
                                               Id = libraryId,
                                               Name = libraryId,
                                               Hint = "h",
                                               CurrentVersion = "v1",
                                               AllVersions = ["v1"]
                                           },
                                       ct);
        await mRepo.UpsertVersionAsync(MakeVersion(libraryId, "v1"), ct);
        PageRecord source = MakePage(libraryId, "v1", "https://x/source");
        await mContext.Pages.InsertOneAsync(source, cancellationToken: ct);
        string suffix = source.Id[(source.Id.LastIndexOf('/') + 1)..];
        await mContext.Pages.InsertOneAsync(source with
                                                {
                                                    Id = $"{libraryId}/v2/{suffix}",
                                                    LibraryId = "malformed-orphan",
                                                    Version = "malformed-version"
                                                },
                                            cancellationToken: ct);

        RenameLibraryOutcome outcome = await mRenameData.PreflightVersionRenameAsync(libraryId,
                                                                                       "v1",
                                                                                       "v2",
                                                                                       ct);

        Assert.Equal(RenameLibraryOutcome.Collision, outcome);
    }

    [Fact]
    public async Task RenameVersionOfNonCurrentLeavesCurrentVersionUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var lib = $"rv-nc-{Guid.NewGuid():N}";
        await mRepo.UpsertLibraryAsync(new LibraryRecord
                                           { Id = lib, Name = lib, Hint = "h",
                                             CurrentVersion = "v9", AllVersions = ["v8", "v9"] }, ct);
        await mRepo.UpsertVersionAsync(MakeVersion(lib, "v8"), ct);
        await mRepo.UpsertVersionAsync(MakeVersion(lib, "v9"), ct);

        RenameLibraryResult counts = await ApplyVersionRenameAsync(lib, "v8", "v8-archived", ct);

        Assert.Equal(1, counts.Versions);
        var libRec = await mRepo.GetLibraryAsync(lib, ct);
        Assert.NotNull(libRec);
        Assert.Equal("v9", libRec.CurrentVersion);
        Assert.Contains("v8-archived", libRec.AllVersions);
        Assert.DoesNotContain("v8", libRec.AllVersions);
    }

    private static LibraryVersionRecord MakeVersion(string lib, string ver, string? previousVersion = null) =>
        new()
            {
                Id = $"{lib}/{ver}", LibraryId = lib, Version = ver, ScrapedAt = DateTime.UtcNow,
                PageCount = 1, ChunkCount = 0, EmbeddingProviderId = "onnx",
                EmbeddingModelName = "nomic-embed-text-v1.5", EmbeddingDimensions = 768,
                PreviousVersion = previousVersion
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
            Host = "library",
            DiscoveredAt = DateTime.UtcNow,
            Status = AuditStatus.Indexed
        };

    private async Task<RenameLibraryResult> ApplyVersionRenameAsync(string libraryId,
                                                                    string sourceVersion,
                                                                    string targetVersion,
                                                                    CancellationToken ct)
    {
        string operationId = $"rename-version-{Guid.NewGuid():N}";
        DateTime reservedAtUtc = DateTime.UtcNow;
        var operation = new LibraryRenameOperationRecord
                            {
                                Id = libraryId,
                                OperationId = operationId,
                                Kind = LibraryRenameOperationKind.Version,
                                State = LibraryRenameOperationState.Applying,
                                Mode = LibraryIngestionMode.Web,
                                SourceLibraryId = libraryId,
                                TargetLibraryId = libraryId,
                                SourceVersion = sourceVersion,
                                TargetVersion = targetVersion,
                                SourceOwnershipReservedAtUtc = reservedAtUtc,
                                TargetOwnershipReservedAtUtc = reservedAtUtc,
                                StartedAtUtc = DateTime.UtcNow,
                                UpdatedAtUtc = DateTime.UtcNow
                            };
        await mContext.LibraryIngestionModes.InsertOneAsync(new LibraryIngestionModeRecord
                                                                 {
                                                                     Id = libraryId,
                                                                     Mode = LibraryIngestionMode.Web,
                                                                     OwnershipState =
                                                                         LibraryIngestionOwnershipState.Committed,
                                                                     LeaseOwnerToken = "rename-test-owner",
                                                                     LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
                                                                     PendingRenameOperationId = operationId,
                                                                     ReservedAtUtc = reservedAtUtc,
                                                                     CommittedAtUtc = DateTime.UtcNow,
                                                                     UpdatedAtUtc = DateTime.UtcNow
                                                                 },
                                                             cancellationToken: ct);
        await mRenameData.PrepareDirectoryDefinitionsAsync(operation, ct);
        RenameLibraryResult result = await mRenameData.ApplyVersionRenameAsync(operation, ct);
        RenameLibraryResult retry = await mRenameData.ApplyVersionRenameAsync(operation, ct);
        Assert.Equal(result, retry);
        return result;
    }

    private const string TestConnectionString = "mongodb://localhost:27017";
}
