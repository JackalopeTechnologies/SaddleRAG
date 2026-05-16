// McpToolJsonShapeTestsPart2.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.Json.Nodes;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

/// <summary>
///     Second batch of MCP tool JSON-shape guards (issue #55), covering the
///     job-status family (get_scrape_status / list_scrape_jobs /
///     get_rescrub_status / get_reembed_status) and the symbol-management
///     family (list_excluded_symbols / add_to_likely_symbols /
///     add_to_stoplist). The search_docs / get_class_reference /
///     get_library_overview tools depend on a fanout of providers
///     (IVectorSearchProvider, IEmbeddingProvider, IReRanker, IQueryMetrics
///     plus the orchestration in SearchTools) that is better exercised by
///     integration tests; the search-related shapes are validated end-to-end
///     in those tests rather than here.
/// </summary>
public sealed class McpToolJsonShapeTestsPart2
{
    private static (RepositoryFactory factory,
        ILibraryProfileRepository profileRepo,
        IExcludedSymbolsRepository excludedRepo,
        IJobRepository jobRepo) MakeFactory()
    {
        var factory = Substitute.For<RepositoryFactory>([null]);
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var jobRepo = Substitute.For<IJobRepository>();
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(profileRepo);
        factory.GetExcludedSymbolsRepository(Arg.Any<string?>()).Returns(excludedRepo);
        factory.GetJobRepository(Arg.Any<string?>()).Returns(jobRepo);
        return (factory, profileRepo, excludedRepo, jobRepo);
    }

    private static JobRecord ScrapeJobRecord(string id, JobStatus status = JobStatus.Completed) =>
        new JobRecord
            {
                Id = id,
                JobType = JobType.Scrape,
                LibraryId = "lib",
                Version = "v1",
                Status = status,
                CreatedAt = DateTime.UtcNow,
                ScrapeProgress = new ScrapeProgress
                                     {
                                         PagesQueued = 1,
                                         PagesFetched = 10,
                                         PagesClassified = 9,
                                         ChunksGenerated = 80,
                                         ChunksEmbedded = 80,
                                         ChunksCompleted = 80,
                                         PagesCompleted = 9
                                     },
                PipelineState = nameof(ScrapeJobStatus.Completed)
            };

    [Fact]
    public async Task GetScrapeStatusShapeContainsAllDocumentedFieldsForExistingJob()
    {
        (var factory, var _, var _, var jobRepo) = MakeFactory();
        jobRepo.GetAsync("job-1", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<JobRecord?>(ScrapeJobRecord("job-1")));

        var json = await IngestionTools.GetScrapeStatus(factory,
                                                        jobId: "job-1",
                                                        profile: null,
                                                        TestContext.Current.CancellationToken
                                                       );
        var root = JsonNode.Parse(json) as JsonObject;

        Assert.NotNull(root);
        foreach(var key in GetScrapeStatusFields)
            Assert.True(root.ContainsKey(key), $"get_scrape_status missing '{key}' field");
        Assert.Equal("job-1", root["Id"]?.GetValue<string>());
        Assert.Equal("Completed", root["Status"]?.GetValue<string>());
        Assert.Equal("lib", root["Library"]?.GetValue<string>());
        Assert.Equal("v1", root["Version"]?.GetValue<string>());
        Assert.Equal(80, root["ChunksEmbedded"]?.GetValue<int>());
    }

    [Fact]
    public async Task GetScrapeStatusReturnsNotFoundMessageWhenJobMissing()
    {
        (var factory, var _, var _, var jobRepo) = MakeFactory();
        jobRepo.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<JobRecord?>(null));

        var result = await IngestionTools.GetScrapeStatus(factory,
                                                          jobId: "missing",
                                                          profile: null,
                                                          TestContext.Current.CancellationToken
                                                         );

        Assert.Contains("missing", result);
        Assert.Contains("No scrape job found", result);
    }

    [Fact]
    public async Task ListScrapeJobsShapeIsArrayOfJobSummaryObjects()
    {
        (var factory, var _, var _, var jobRepo) = MakeFactory();
        jobRepo.ListRecentAsync(JobType.Scrape, Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns([ScrapeJobRecord("a"), ScrapeJobRecord("b", JobStatus.Running)]);

        var json = await IngestionTools.ListScrapeJobs(factory,
                                                       limit: 20,
                                                       profile: null,
                                                       TestContext.Current.CancellationToken
                                                      );
        var array = JsonNode.Parse(json) as JsonArray;

        Assert.NotNull(array);
        Assert.Equal(2, array.Count);
        var first = array[0] as JsonObject;
        Assert.NotNull(first);
        foreach(var key in ListScrapeJobsEntryFields)
            Assert.True(first.ContainsKey(key), $"list_scrape_jobs entry missing '{key}' field");
        Assert.Equal("a", first["Id"]?.GetValue<string>());
    }

    [Fact]
    public async Task ListExcludedSymbolsShapeIsReconNeededShapeWhenLibraryProfileMissing()
    {
        (var factory, var profileRepo, var _, var _) = MakeFactory();
        profileRepo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<LibraryProfile?>(null));

        var json = await SymbolManagementTools.ListExcludedSymbols(factory,
                                                                   library: "lib",
                                                                   version: "v1",
                                                                   reason: null,
                                                                   limit: 50,
                                                                   profile: null,
                                                                   TestContext.Current.CancellationToken
                                                                  );
        var root = JsonNode.Parse(json) as JsonObject;

        Assert.NotNull(root);
        Assert.True(root.ContainsKey("ReconNeeded"));
        Assert.True(root["ReconNeeded"]?.GetValue<bool>());
        Assert.Equal("lib", root["Library"]?.GetValue<string>());
        Assert.Equal("v1", root["Version"]?.GetValue<string>());
    }

    [Fact]
    public async Task ListExcludedSymbolsShapeIsLibraryVersionItemsArrayWhenProfilePresent()
    {
        (var factory, var profileRepo, var excludedRepo, var _) = MakeFactory();
        profileRepo.GetAsync("lib", "v1", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<LibraryProfile?>(new LibraryProfile
                                                                 {
                                                                     Id = "lib/v1",
                                                                     LibraryId = "lib",
                                                                     Version = "v1",
                                                                     Languages = ["csharp"],
                                                                     LikelySymbols = [],
                                                                     Stoplist = []
                                                                 }
                                                            )
                           );
        excludedRepo.ListAsync(Arg.Any<string>(),
                               Arg.Any<string>(),
                               Arg.Any<SymbolRejectionReason?>(),
                               Arg.Any<int>(),
                               Arg.Any<CancellationToken>()
                              )
                    .Returns(Task.FromResult<IReadOnlyList<ExcludedSymbol>>([
                                          new ExcludedSymbol
                                              {
                                                  Id = "lib/v1/Foo",
                                                  LibraryId = "lib",
                                                  Version = "v1",
                                                  Name = "Foo",
                                                  Reason = SymbolRejectionReason.LikelyAbbreviation,
                                                  ChunkCount = 3,
                                                  SampleSentences = ["snippet one", "snippet two"]
                                              }
                                      ]
                                     )
                            );
        excludedRepo.CountAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(1));

        var json = await SymbolManagementTools.ListExcludedSymbols(factory,
                                                                   library: "lib",
                                                                   version: "v1",
                                                                   reason: null,
                                                                   limit: 50,
                                                                   profile: null,
                                                                   TestContext.Current.CancellationToken
                                                                  );
        var root = JsonNode.Parse(json) as JsonObject;

        Assert.NotNull(root);
        Assert.Equal("lib", root["Library"]?.GetValue<string>());
        Assert.Equal("v1", root["Version"]?.GetValue<string>());
        Assert.Equal(1, root["TotalExcluded"]?.GetValue<int>());
        Assert.Equal(1, root["Returned"]?.GetValue<int>());
        var items = root["Items"] as JsonArray;
        Assert.NotNull(items);
        Assert.Single(items);
        var item = items[0] as JsonObject;
        Assert.NotNull(item);
        Assert.Equal("Foo", item["Name"]?.GetValue<string>());
        Assert.Equal("LikelyAbbreviation", item["Reason"]?.GetValue<string>());
        Assert.Equal(3, item["ChunkCount"]?.GetValue<int>());
        Assert.NotNull(item["SampleSentences"] as JsonArray);
    }

    [Fact]
    public async Task AddToLikelySymbolsShapeIsReconNeededWhenLibraryProfileMissing()
    {
        (var factory, var profileRepo, var _, var _) = MakeFactory();
        profileRepo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<LibraryProfile?>(null));

        var json = await SymbolManagementTools.AddToLikelySymbols(factory,
                                                                  library: "lib",
                                                                  version: "v1",
                                                                  names: ["Foo"],
                                                                  profile: null,
                                                                  TestContext.Current.CancellationToken
                                                                 );
        var root = JsonNode.Parse(json) as JsonObject;

        Assert.NotNull(root);
        Assert.True(root["ReconNeeded"]?.GetValue<bool>());
    }

    [Fact]
    public async Task AddToStoplistShapeIsReconNeededWhenLibraryProfileMissing()
    {
        (var factory, var profileRepo, var _, var _) = MakeFactory();
        profileRepo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<LibraryProfile?>(null));

        var json = await SymbolManagementTools.AddToStoplist(factory,
                                                             library: "lib",
                                                             version: "v1",
                                                             names: ["bar"],
                                                             profile: null,
                                                             TestContext.Current.CancellationToken
                                                            );
        var root = JsonNode.Parse(json) as JsonObject;

        Assert.NotNull(root);
        Assert.True(root["ReconNeeded"]?.GetValue<bool>());
    }

    private static readonly string[] GetScrapeStatusFields =
        [
            "Id", "Status", "PipelineState", "PagesQueued", "PagesFetched", "PagesClassified",
            "ChunksGenerated", "ChunksEmbedded", "ChunksCompleted", "PagesCompleted", "ErrorCount",
            "ErrorMessage", "CreatedAt", "StartedAt", "CompletedAt", "Library", "Version"
        ];

    private static readonly string[] ListScrapeJobsEntryFields =
        ["Id", "Status", "PipelineState", "Library", "Version", "CreatedAt", "CompletedAt"];
}
