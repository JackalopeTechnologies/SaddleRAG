// IngestToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class IngestToolsTests
{
    [Theory]
    [InlineData(VersionPublicationState.Published)]
    [InlineData(VersionPublicationState.Building)]
    public async Task StartIngestSelectsSuspectRecoveryByPublicationState(VersionPublicationState state)
    {
        var contexts = new SaddleRagDbContextFactory(Options.Create(new SaddleRagDbSettings()));
        var factory = Substitute.For<RepositoryFactory>(contexts);
        var libraries = Substitute.For<ILibraryRepository>();
        var chunks = Substitute.For<IChunkRepository>();
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraries);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunks);
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(Substitute.For<ILibraryProfileRepository>());
        factory.GetJobRepository(Arg.Any<string?>()).Returns(Substitute.For<IJobRepository>());
        libraries.GetVersionAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(new LibraryVersionRecord
        {
            Id = "foo/1.0", LibraryId = "foo", Version = "1.0", ScrapedAt = DateTime.UtcNow,
            PageCount = 1, ChunkCount = 1, EmbeddingProviderId = "test", EmbeddingModelName = "test", EmbeddingDimensions = 1,
            PublicationState = state, Suspect = true, SuspectReasons = ["Wrong source"]
        });
        chunks.GetSampleTitlesAsync("foo", "1.0", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(["Wrong article"]);
        chunks.GetHostnameDistributionAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(new Dictionary<string, int>());

        string json = await IngestTools.StartIngest(factory, "https://docs.example.com", "foo", "1.0",
            ct: TestContext.Current.CancellationToken);
        JsonObject response = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        JsonObject arguments = Assert.IsType<JsonObject>(response["NextToolArgs"]);

        Assert.Equal("UrlSuspect", response["StatusName"]?.GetValue<string>());
        if (state == VersionPublicationState.Published)
        {
            Assert.Equal("scrape_docs", response["NextTool"]?.GetValue<string>());
            Assert.Null(arguments["version"]);
            Assert.Contains("explicit new version identifier", response["Message"]?.GetValue<string>());
        }
        else
        {
            Assert.Equal("submit_url_correction", response["NextTool"]?.GetValue<string>());
            Assert.Equal("1.0", arguments["version"]?.GetValue<string>());
        }
    }

    [Fact]
    public void MakeReadyToScrapeIncludesExcludedPatternsWhenSupplied()
    {
        var response = IngestTools.MakeReadyToScrape("foo",
                                                     "1.0",
                                                     "https://docs.example.com",
                                                     "ready",
                                                         ["/account/login", "/account/register"]
                                                    );

        Assert.Equal(IngestStatus.ReadyToScrape, response.Status);
        Assert.Equal(["/account/login", "/account/register"], response.RecommendedExcludedUrlPatterns);
    }

    [Fact]
    public void MakeReadyToScrapeHandlesEmptyPatterns()
    {
        var response = IngestTools.MakeReadyToScrape("foo",
                                                     "1.0",
                                                     "https://docs.example.com",
                                                     "ready",
                                                         []
                                                    );

        Assert.Empty(response.RecommendedExcludedUrlPatterns);
    }

    [Fact]
    public void MakeReadyToScrapePopulatesNextToolArgsForScrapeDocs()
    {
        var response = IngestTools.MakeReadyToScrape("foo",
                                                     "1.0",
                                                     "https://docs.example.com",
                                                     "ready",
                                                         ["/account/login"]
                                                    );

        Assert.Equal("scrape_docs", response.NextTool);
        Assert.Equal("https://docs.example.com", response.NextToolArgs["url"]);
        Assert.Equal("foo", response.NextToolArgs["libraryId"]);
        Assert.Equal("1.0", response.NextToolArgs["version"]);
    }
}
