// RenameVersionIntegrationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Options;
using MongoDB.Driver;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
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
                                              DatabaseName = TestDatabaseName
                                          });
        mContext = new SaddleRagDbContext(settings);
        mRepo = new LibraryRepository(mContext);
    }

    private readonly SaddleRagDbContext mContext;
    private readonly LibraryRepository mRepo;

    public async ValueTask InitializeAsync() =>
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
        await mContext.Pages.InsertOneAsync(MakePage(lib, "current", "https://x/p1"), cancellationToken: ct);

        var response = await mRepo.RenameVersionAsync(lib, "current", "v8", ct);

        Assert.Equal(RenameLibraryOutcome.Renamed, response.Outcome);
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

        var response = await mRepo.RenameVersionAsync(lib, "v8", "v9", ct);

        Assert.Equal(RenameLibraryOutcome.Collision, response.Outcome);
        Assert.NotNull(await mRepo.GetVersionAsync(lib, "v8", ct));
    }

    [Fact]
    public async Task RenameVersionReturnsNotFoundWhenSourceMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        var lib = $"rv-nf-{Guid.NewGuid():N}";
        var response = await mRepo.RenameVersionAsync(lib, "nope", "v8", ct);
        Assert.Equal(RenameLibraryOutcome.NotFound, response.Outcome);
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

        var response = await mRepo.RenameVersionAsync(lib, "v8", "v8-archived", ct);

        Assert.Equal(RenameLibraryOutcome.Renamed, response.Outcome);
        var libRec = await mRepo.GetLibraryAsync(lib, ct);
        Assert.NotNull(libRec);
        Assert.Equal("v9", libRec.CurrentVersion);
        Assert.Contains("v8-archived", libRec.AllVersions);
        Assert.DoesNotContain("v8", libRec.AllVersions);
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

    private const string TestConnectionString = "mongodb://localhost:27017";
    private const string TestDatabaseName = "SaddleRAG_test_rename";
}
