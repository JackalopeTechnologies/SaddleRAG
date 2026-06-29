// RenameLibraryIntegrationTests.cs
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
public sealed class RenameLibraryIntegrationTests : IAsyncLifetime
{
    public RenameLibraryIntegrationTests()
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

        var response = await mRepo.RenameAsync(oldId, newId, ct);

        Assert.Equal(RenameLibraryOutcome.Renamed, response.Outcome);
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
