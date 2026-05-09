// ReconToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Recon;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class ReconToolsTests
{
    [Fact]
    public void ReconLibraryPayloadMentionsCrawlHints()
    {
        var json = ReconTools.ReconLibrary("https://docs.example.com", "example", "1.0");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var instructions = root.GetProperty("Instructions").GetString();
        Assert.NotNull(instructions);
        Assert.Contains("crawlHints", instructions, StringComparison.OrdinalIgnoreCase);

        var schemaText = root.GetProperty("JsonSchema").GetString();
        Assert.NotNull(schemaText);
        Assert.Contains("crawlHints", schemaText, StringComparison.Ordinal);
        Assert.Contains("excludedUrlPatterns", schemaText, StringComparison.Ordinal);
        Assert.Contains("expectedHosts", schemaText, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconLibraryPayloadMentionsDryrun()
    {
        var json = ReconTools.ReconLibrary("https://docs.example.com", "example", "1.0");
        using var doc = JsonDocument.Parse(json);

        var instructions = doc.RootElement.GetProperty("Instructions").GetString();
        Assert.NotNull(instructions);
        Assert.Contains("dryrun_scrape", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitLibraryProfileParsesCrawlHints()
    {
        var repo = Substitute.For<ILibraryProfileRepository>();
        repo.ListAllAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LibraryProfile>());
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(repo);
        var service = new LibraryProfileService(NullLogger<LibraryProfileService>.Instance);

        const string profileJson = """
                                   {
                                     "languages": ["C#"],
                                     "casing": { "types": "PascalCase" },
                                     "separators": ["."],
                                     "callableShapes": ["Foo()"],
                                     "likelySymbols": ["Bar"],
                                     "crawlHints": {
                                       "excludedUrlPatterns": ["/account/login", "/account/register"],
                                       "expectedHosts": ["docs.example.com"],
                                       "notes": "API ref auth-walled"
                                     },
                                     "confidence": 0.9,
                                     "source": "calling-llm"
                                   }
                                   """;

        var resultJson = await ReconTools.SubmitLibraryProfile(service,
                                                               factory,
                                                               "example",
                                                               "1.0",
                                                               profileJson,
                                                               profile: null,
                                                               TestContext.Current.CancellationToken
                                                              );

        await repo.Received(requiredNumberOfCalls: 1)
                  .UpsertAsync(Arg.Is<LibraryProfile>(p =>
                                                          p.CrawlHints.ExcludedUrlPatterns.Count == 2 &&
                                                          p.CrawlHints.ExcludedUrlPatterns[0] == "/account/login" &&
                                                          p.CrawlHints.ExpectedHosts.Count == 1 &&
                                                          p.CrawlHints.Notes == "API ref auth-walled"
                                                     ),
                               Arg.Any<CancellationToken>()
                              );
        Assert.Contains("\"LibraryId\":", resultJson);
    }

    [Fact]
    public async Task SubmitLibraryProfileTreatsMissingCrawlHintsAsEmpty()
    {
        var repo = Substitute.For<ILibraryProfileRepository>();
        repo.ListAllAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LibraryProfile>());
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(repo);
        var service = new LibraryProfileService(NullLogger<LibraryProfileService>.Instance);

        const string profileJson = """
                                   {
                                     "languages": ["C#"],
                                     "casing": { "types": "PascalCase" },
                                     "separators": ["."],
                                     "callableShapes": ["Foo()"],
                                     "likelySymbols": ["Bar"],
                                     "confidence": 0.9,
                                     "source": "calling-llm"
                                   }
                                   """;

        await ReconTools.SubmitLibraryProfile(service,
                                              factory,
                                              "example",
                                              "1.0",
                                              profileJson,
                                              profile: null,
                                              TestContext.Current.CancellationToken
                                             );

        await repo.Received(requiredNumberOfCalls: 1)
                  .UpsertAsync(Arg.Is<LibraryProfile>(p =>
                                                          p.CrawlHints.ExcludedUrlPatterns.Count == 0 &&
                                                          p.CrawlHints.ExpectedHosts.Count == 0 &&
                                                          p.CrawlHints.Notes == string.Empty
                                                     ),
                               Arg.Any<CancellationToken>()
                              );
    }
}
