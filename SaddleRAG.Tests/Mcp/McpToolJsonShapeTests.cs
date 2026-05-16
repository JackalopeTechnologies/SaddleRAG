// McpToolJsonShapeTests.cs
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
///     JSON-shape guards on MCP tool responses. Existing tool tests use
///     substring checks (e.g. <c>Assert.Contains("\"libraryCount\":", json)</c>)
///     which catch some regressions but pass on subtle renames (e.g.
///     <c>chunkCount</c> -> <c>ChunkCount</c>) that break downstream consumers.
///     These tests parse the JSON into a <see cref="JsonNode" /> and assert
///     field presence + types so a field-rename regression fails loudly.
/// </summary>
public sealed class McpToolJsonShapeTests
{
    [Fact]
    public async Task ListLibrariesNonEmptyShapeIsArrayOfLibraryRecordObjects()
    {
        (var factory, var libraryRepo, var _, var _) = MakeFactory();
        libraryRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>())
                   .Returns([
                                    new LibraryRecord
                                        {
                                            Id = "foo",
                                            Name = "foo-lib",
                                            Hint = "test lib",
                                            CurrentVersion = "1.0",
                                            AllVersions = ["1.0", "0.9"]
                                        }
                                ]
                           );

        var json = await LibraryTools.ListLibraries(factory, profile: null, TestContext.Current.CancellationToken);
        var root = JsonNode.Parse(json);

        Assert.NotNull(root);
        var array = root as JsonArray;
        Assert.NotNull(array);
        Assert.Single(array);
        var entry = array[index: 0] as JsonObject;
        Assert.NotNull(entry);
        Assert.Equal("foo", entry["Id"]?.GetValue<string>());
        Assert.Equal("foo-lib", entry["Name"]?.GetValue<string>());
        Assert.Equal("test lib", entry["Hint"]?.GetValue<string>());
        Assert.Equal("1.0", entry["CurrentVersion"]?.GetValue<string>());
        Assert.Equal(expected: 2, (entry["AllVersions"] as JsonArray)?.Count);
    }

    [Fact]
    public async Task ListLibrariesEmptyShapeWrapsArrayInLibrariesAndHintFields()
    {
        (var factory, var libraryRepo, var _, var _) = MakeFactory();
        libraryRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>()).Returns([]);

        var json = await LibraryTools.ListLibraries(factory, profile: null, TestContext.Current.CancellationToken);
        var root = JsonNode.Parse(json) as JsonObject;

        Assert.NotNull(root);
        Assert.True(root.ContainsKey("Libraries"));
        Assert.True(root.ContainsKey("Hint"));
        Assert.Empty(root["Libraries"] as JsonArray ?? []);
        Assert.False(string.IsNullOrEmpty(root["Hint"]?.GetValue<string>()));
    }

    [Fact]
    public async Task ListSymbolsShapeIsArrayOfNameAndKindObjects()
    {
        (var factory, var libraryRepo, var chunkRepo, var _) = MakeFactory();
        libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = "foo",
                                    Name = "f",
                                    Hint = "h",
                                    CurrentVersion = "1.0",
                                    AllVersions = ["1.0"]
                                }
                           );
        chunkRepo.GetAllSymbolsAsync("foo", "1.0", filter: null, Arg.Any<CancellationToken>())
                 .Returns([
                                  new Symbol { Name = "ClassA", Kind = SymbolKind.Type },
                                  new Symbol { Name = "FuncB", Kind = SymbolKind.Function }
                              ]
                         );

        var json = await LibraryTools.ListSymbols(factory,
                                                  "foo",
                                                  kind: null,
                                                  ct: TestContext.Current.CancellationToken
                                                 );
        var array = JsonNode.Parse(json) as JsonArray;

        Assert.NotNull(array);
        Assert.Equal(expected: 2, array.Count);
        foreach(var entry in array.OfType<JsonObject>())
        {
            Assert.True(entry.ContainsKey("name"), "list_symbols entry missing 'name'");
            Assert.True(entry.ContainsKey("kind"), "list_symbols entry missing 'kind'");
        }
        Assert.Equal("ClassA", array[index: 0]?["name"]?.GetValue<string>());
        Assert.Equal("class", array[index: 0]?["kind"]?.GetValue<string>());
    }

    [Fact]
    public async Task GetLibraryHealthShapeContainsAllDocumentedFields()
    {
        (var factory, var libraryRepo, var chunkRepo, var _) = MakeFactory();
        libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = "foo",
                                    Name = "f",
                                    Hint = "h",
                                    CurrentVersion = "1.0",
                                    AllVersions = ["1.0"]
                                }
                           );
        libraryRepo.GetVersionAsync("foo", "1.0", Arg.Any<CancellationToken>())
                   .Returns(new LibraryVersionRecord
                                {
                                    Id = "foo/1.0",
                                    LibraryId = "foo",
                                    Version = "1.0",
                                    ScrapedAt = DateTime.UtcNow,
                                    PageCount = 50,
                                    ChunkCount = 250,
                                    EmbeddingProviderId = "ollama",
                                    EmbeddingModelName = "nomic-embed-text",
                                    EmbeddingDimensions = 768,
                                    BoundaryIssuePct = 2.0,
                                    Suspect = false,
                                    SuspectReasons = []
                                }
                           );
        chunkRepo.GetLanguageMixAsync("foo", "1.0", Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<string, double> { ["csharp"] = 1.0 });
        chunkRepo.GetHostnameDistributionAsync("foo", "1.0", Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<string, int> { ["docs.foo.com"] = 50 });

        var json = await HealthTools.GetLibraryHealth(factory,
                                                      "foo",
                                                      version: null,
                                                      profile: null,
                                                      TestContext.Current.CancellationToken
                                                     );
        var root = JsonNode.Parse(json) as JsonObject;

        Assert.NotNull(root);
        foreach(var key in HealthDocumentedFields)
            Assert.True(root.ContainsKey(key), $"get_library_health response missing '{key}' field");
        Assert.Equal(expected: 250, root["chunkCount"]?.GetValue<int>());
        Assert.Equal(expected: 50, root["pageCount"]?.GetValue<int>());
        Assert.False(root["suspect"]?.GetValue<bool>());
        Assert.NotNull(root["boundaryHint"] as JsonObject);
    }

    [Fact]
    public async Task GetLibraryHealthNotFoundShapeIsErrorObject()
    {
        (var factory, var libraryRepo, var _, var _) = MakeFactory();
        libraryRepo.GetLibraryAsync("missing", Arg.Any<CancellationToken>()).Returns((LibraryRecord?) null);

        var json = await HealthTools.GetLibraryHealth(factory,
                                                      "missing",
                                                      version: null,
                                                      profile: null,
                                                      TestContext.Current.CancellationToken
                                                     );
        var root = JsonNode.Parse(json) as JsonObject;

        Assert.NotNull(root);
        Assert.True(root.ContainsKey("Error"));
        Assert.Contains("missing", root["Error"]?.GetValue<string>() ?? string.Empty);
    }

    [Fact]
    public async Task GetDashboardIndexShapeContainsTopLevelDocumentedFields()
    {
        (var factory, var libraryRepo, var _, var _) = MakeFactory();
        libraryRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>()).Returns([]);

        var json = await HealthTools.GetDashboardIndex(factory,
                                                       profile: null,
                                                       TestContext.Current.CancellationToken
                                                      );
        var root = JsonNode.Parse(json) as JsonObject;

        Assert.NotNull(root);
        foreach(var key in DashboardDocumentedFields)
            Assert.True(root.ContainsKey(key), $"get_dashboard_index response missing '{key}' field");
        Assert.Equal(expected: 0, root["libraryCount"]?.GetValue<int>());
        Assert.NotNull(root["recentJobs"] as JsonArray);
        Assert.NotNull(root["suspectLibraries"] as JsonArray);
        var suggested = root["suggestedNextAction"] as JsonObject;
        Assert.NotNull(suggested);
        Assert.True(suggested.ContainsKey("tool"));
        Assert.True(suggested.ContainsKey("message"));
    }

    [Fact]
    public async Task GetDashboardIndexRecentJobsEntryShapeMatchesContract()
    {
        (var factory, var libraryRepo, var _, var jobRepo) = MakeFactory();
        libraryRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>()).Returns([]);
        jobRepo.ListRecentAsync(Arg.Any<JobType?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns([
                                MakeJobRecord("job-1", "foo", "1.0", JobStatus.Completed, DateTime.UtcNow,
                                              lastProgressAt: null
                                             )
                            ]
                       );

        var json = await HealthTools.GetDashboardIndex(factory,
                                                       profile: null,
                                                       TestContext.Current.CancellationToken
                                                      );
        var entry = ((JsonNode.Parse(json) as JsonObject)?["recentJobs"] as JsonArray)?[0] as JsonObject;

        Assert.NotNull(entry);
        foreach(var key in DashboardRecentJobEntryFields)
            Assert.True(entry.ContainsKey(key), $"recentJobs entry missing '{key}' field");
        Assert.Equal("job-1", entry["Id"]?.GetValue<string>());
        Assert.Equal("foo", entry["Library"]?.GetValue<string>());
        Assert.False(entry["Stale"]?.GetValue<bool>());
    }

    private static readonly string[] HealthDocumentedFields =
        [
            "library", "version", "currentVersion", "lastScrapedAt", "chunkCount", "pageCount",
            "distinctHostCount", "hostnames", "languageMix", "boundaryIssuePct", "suspect",
            "suspectReasons", "boundaryHint"
        ];

    private static readonly string[] DashboardDocumentedFields =
        [
            "libraryCount", "versionCount", "recentJobs", "suspectCount", "suspectLibraries",
            "suggestedNextAction"
        ];

    private static readonly string[] DashboardRecentJobEntryFields =
        ["Id", "JobType", "Status", "PipelineState", "Library", "Version", "Stale", "LastProgressAt"];

    private static JobRecord MakeJobRecord(string id,
                                           string libraryId,
                                           string version,
                                           JobStatus status,
                                           DateTime createdAt,
                                           DateTime? lastProgressAt) =>
        new JobRecord
            {
                Id = id,
                JobType = JobType.Scrape,
                LibraryId = libraryId,
                Version = version,
                Status = status,
                CreatedAt = createdAt,
                LastProgressAt = lastProgressAt
            };

    private static (RepositoryFactory factory,
        ILibraryRepository libraryRepo,
        IChunkRepository chunkRepo,
        IJobRepository jobRepo) MakeFactory()
    {
        var factory = Substitute.For<RepositoryFactory>([null]);
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var jobRepo = Substitute.For<IJobRepository>();
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        factory.GetJobRepository(Arg.Any<string?>()).Returns(jobRepo);
        jobRepo.ListRecentAsync(Arg.Any<JobType?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        jobRepo.ListRunningAsync(Arg.Any<JobType?>(), Arg.Any<CancellationToken>()).Returns([]);
        return (factory, libraryRepo, chunkRepo, jobRepo);
    }
}
