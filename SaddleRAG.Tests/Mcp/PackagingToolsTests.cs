// PackagingToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Mcp.Tools;
using SaddleRAG.Packaging;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class PackagingToolsTests
{
    [Fact]
    public void PackagingToolsClassHasMcpServerToolTypeAttribute()
    {
        var attr = typeof(PackagingTools).GetCustomAttribute<McpServerToolTypeAttribute>();
        Assert.NotNull(attr);
    }

    [Fact]
    public void ExportLibraryIsDiscoverableAsMcpTool()
    {
        var method = typeof(PackagingTools).GetMethod(nameof(PackagingTools.ExportLibrary));
        Assert.NotNull(method);
        var attr = method.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("export_library", attr.Name);
    }

    [Fact]
    public void ImportLibraryIsDiscoverableAsMcpTool()
    {
        var method = typeof(PackagingTools).GetMethod(nameof(PackagingTools.ImportLibrary));
        Assert.NotNull(method);
        var attr = method.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("import_library", attr.Name);
    }

    [Fact]
    public void ExportLibraryHasDescriptionOnEveryUserFacingParameter()
    {
        var method = typeof(PackagingTools).GetMethod(nameof(PackagingTools.ExportLibrary));
        Assert.NotNull(method);
        var parameters = method.GetParameters();
        foreach (var p in parameters.Where(p => p.ParameterType != typeof(LibraryExporter) &&
                                                p.ParameterType != typeof(CancellationToken)))
        {
            var desc = p.GetCustomAttribute<DescriptionAttribute>();
            Assert.NotNull(desc);
            Assert.False(string.IsNullOrEmpty(desc.Description), $"Parameter {p.Name} has empty description");
        }
    }

    [Fact]
    public async Task CompletedReembedIndexIsNotOverwrittenByStaleImportReload()
    {
        var jobs = Substitute.For<IJobRepository>();
        jobs.GetAsync(ReembedJobId, Arg.Any<CancellationToken>())
            .Returns(new JobRecord
                         {
                             Id = ReembedJobId,
                             JobType = JobType.Reembed,
                             Profile = Profile,
                             LibraryId = LibraryId,
                             Version = ReembedVersion,
                             InputJson = "{}",
                             Status = JobStatus.Completed,
                             ItemsLabel = "chunks"
                         });

        var chunks = Substitute.For<IChunkRepository>();
        chunks.GetChunksAsync(LibraryId, ReembedVersion, Arg.Any<CancellationToken>())
              .Returns([Chunk(ReembedVersion, embedding: null)]);
        chunks.GetChunksAsync(LibraryId, MatchingVersion, Arg.Any<CancellationToken>())
              .Returns([Chunk(MatchingVersion, [2.0f])]);
        var repositories = Substitute.For<RepositoryFactory>([null!]);
        repositories.GetJobRepository(Profile).Returns(jobs);
        repositories.GetChunkRepository(Profile).Returns(chunks);

        var indexed = new Dictionary<string, IReadOnlyList<DocChunk>>(StringComparer.Ordinal)
                          {
                              [ReembedVersion] = [Chunk(ReembedVersion, [9.0f])]
                          };
        var vectorSearch = Substitute.For<IVectorSearchProvider>();
        vectorSearch.IndexChunksAsync(Profile,
                                      LibraryId,
                                      Arg.Any<string>(),
                                      Arg.Any<IReadOnlyList<DocChunk>>(),
                                      Arg.Any<CancellationToken>())
                    .Returns(call =>
                             {
                                 indexed[call.ArgAt<string>(position: 2)] =
                                     call.ArgAt<IReadOnlyList<DocChunk>>(position: 3);
                                 return Task.CompletedTask;
                             });
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        var runner = new ScrapeJobRunner(null!,
                                         chunks,
                                         vectorSearch,
                                         Substitute.For<ILibraryRepository>(),
                                         NullLogger<ScrapeJobRunner>.Instance,
                                         repositories,
                                         Substitute.For<IJobCancellationRegistry>(),
                                         lifetime);
        var import = new ImportResult
                         {
                             LibraryId = LibraryId,
                             VersionsImported = [ReembedVersion, MatchingVersion],
                             OverwrittenVersions = [],
                             BytesFreed = 0,
                             PendingReembedJobIds = [ReembedJobId],
                             PartialFailures = [],
                             RecommendedFollowUp = string.Empty
                         };

        ImportResult result = await PackagingTools.ReloadImportedLibraryAsync(
                                  repositories,
                                  runner,
                                  NullLogger<PackagingTools.PackagingToolsLog>.Instance,
                                  import,
                                  Profile,
                                  "test.srlib.zip",
                                  TestContext.Current.CancellationToken);

        Assert.Same(import, result);
        float[] preservedEmbedding = Assert.IsType<float[]>(Assert.Single(indexed[ReembedVersion]).Embedding);
        float[] matchingEmbedding = Assert.IsType<float[]>(Assert.Single(indexed[MatchingVersion]).Embedding);
        Assert.Equal([9.0f], preservedEmbedding);
        Assert.Equal([2.0f], matchingEmbedding);
        await chunks.DidNotReceive()
                    .GetChunksAsync(LibraryId, ReembedVersion, Arg.Any<CancellationToken>());
        await chunks.Received(requiredNumberOfCalls: 1)
                    .GetChunksAsync(LibraryId, MatchingVersion, TestContext.Current.CancellationToken);
        await vectorSearch.DidNotReceive()
                          .IndexChunksAsync(Profile,
                                            LibraryId,
                                            ReembedVersion,
                                            Arg.Any<IReadOnlyList<DocChunk>>(),
                                            Arg.Any<CancellationToken>());
    }

    private static DocChunk Chunk(string version, float[]? embedding) => new()
        {
            Id = $"{version}-chunk",
            LibraryId = LibraryId,
            Version = version,
            PageUrl = $"https://example.test/{version}",
            PageTitle = version,
            Category = DocCategory.HowTo,
            Content = version,
            Embedding = embedding,
            TokenCount = 1
        };

    private const string LibraryId = "imported-library";
    private const string MatchingVersion = "matching";
    private const string Profile = "company";
    private const string ReembedJobId = "completed-reembed";
    private const string ReembedVersion = "needs-reembed";
}
