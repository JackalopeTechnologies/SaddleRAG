// McpToolJsonShapeTestsPart3.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

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
///     Final batch of MCP tool JSON-shape guards (issue #55), covering
///     every remaining tool whose response shape is consumer-facing:
///     the rescrub / reembed status + list families and the unified
///     get_job_status / list_jobs surface. Mutation tools (set_*,
///     delete_*, cleanup_*, rename_library) and orchestration tools
///     (start_ingest, scrape_docs, dryrun_scrape, inspect_scrape,
///     cancel_job, add_page, list_pages, index_project_dependencies,
///     reextract_library, recon_library, rechunk_library) deliberately
///     stay out of shape-guard scope: they either return trivial
///     bool/string payloads or kick off background jobs whose state is
///     surfaced via the *_status tools already covered here.
/// </summary>
public sealed class McpToolJsonShapeTestsPart3
{
    private static (RepositoryFactory factory, IJobRepository jobRepo) MakeFactory()
    {
        var factory = Substitute.For<RepositoryFactory>([null]);
        var jobRepo = Substitute.For<IJobRepository>();
        factory.GetJobRepository(Arg.Any<string?>()).Returns(jobRepo);
        return (factory, jobRepo);
    }

    private static JobRecord JobOfType(string id, JobType jobType, string? resultJson = null) =>
        new JobRecord
            {
                Id = id,
                JobType = jobType,
                LibraryId = "lib",
                Version = "v1",
                Status = JobStatus.Completed,
                CreatedAt = DateTime.UtcNow,
                StartedAt = DateTime.UtcNow.AddMinutes(-1),
                CompletedAt = DateTime.UtcNow,
                ItemsProcessed = 100,
                ItemsTotal = 100,
                ItemsLabel = "chunks",
                ResultJson = resultJson
            };

    [Fact]
    public async Task GetRescrubStatusShapeContainsAllDocumentedFieldsForRescrubJob()
    {
        (var factory, var jobRepo) = MakeFactory();
        var resultJson = """{"LibraryId":"lib","Version":"v1","Processed":100,"Changed":42,"BoundaryIssues":5}""";
        jobRepo.GetAsync("rs-1", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<JobRecord?>(JobOfType("rs-1", JobType.Rescrub, resultJson)));

        var json = await IngestionTools.GetRescrubStatus(factory, "rs-1", profile: null, TestContext.Current.CancellationToken);
        var root = JsonNode.Parse(json) as JsonObject;

        Assert.NotNull(root);
        foreach(var key in GetRescrubStatusFields)
            Assert.True(root.ContainsKey(key), $"get_reextract_status missing '{key}' field");
        Assert.Equal("rs-1", root["Id"]?.GetValue<string>());
        Assert.Equal(42, root["ChunksChanged"]?.GetValue<int>());
    }

    [Fact]
    public async Task GetRescrubStatusReturnsNotFoundMessageForNonRescrubJob()
    {
        (var factory, var jobRepo) = MakeFactory();
        jobRepo.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<JobRecord?>(JobOfType("scrape-1", JobType.Scrape)));

        var result =
            await IngestionTools.GetRescrubStatus(factory, "scrape-1", profile: null, TestContext.Current.CancellationToken);

        Assert.Contains("No reextract job found", result);
    }

    [Fact]
    public async Task ListRescrubJobsShapeIsArrayOfRescrubSummaryObjects()
    {
        (var factory, var jobRepo) = MakeFactory();
        jobRepo.ListRecentAsync(JobType.Rescrub, Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns([JobOfType("rs-1", JobType.Rescrub), JobOfType("rs-2", JobType.Rescrub)]);

        var json = await IngestionTools.ListRescrubJobs(factory, limit: 20, profile: null, TestContext.Current.CancellationToken);
        var array = JsonNode.Parse(json) as JsonArray;

        Assert.NotNull(array);
        Assert.Equal(2, array.Count);
        var first = array[0] as JsonObject;
        Assert.NotNull(first);
        foreach(var key in ListRescrubJobsEntryFields)
            Assert.True(first.ContainsKey(key), $"list_reextract_jobs entry missing '{key}' field");
    }

    [Fact]
    public async Task GetReembedStatusShapeContainsAllDocumentedFieldsForReembedJob()
    {
        (var factory, var jobRepo) = MakeFactory();
        jobRepo.GetAsync("re-1", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<JobRecord?>(JobOfType("re-1", JobType.Reembed)));

        var json = await IngestionTools.GetReembedStatus(factory, "re-1", profile: null, TestContext.Current.CancellationToken);
        var root = JsonNode.Parse(json) as JsonObject;

        Assert.NotNull(root);
        foreach(var key in GetReembedStatusFields)
            Assert.True(root.ContainsKey(key), $"get_reembed_status missing '{key}' field");
        Assert.Equal("re-1", root["Id"]?.GetValue<string>());
    }

    [Fact]
    public async Task GetReembedStatusReturnsNotFoundMessageForNonReembedJob()
    {
        (var factory, var jobRepo) = MakeFactory();
        jobRepo.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<JobRecord?>(null));

        var result = await IngestionTools.GetReembedStatus(factory, "missing", profile: null, TestContext.Current.CancellationToken);

        Assert.Contains("No reembed job found", result);
    }

    [Fact]
    public async Task ListReembedJobsShapeIsArrayOfReembedSummaryObjects()
    {
        (var factory, var jobRepo) = MakeFactory();
        jobRepo.ListRecentAsync(JobType.Reembed, Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns([JobOfType("re-1", JobType.Reembed)]);

        var json = await IngestionTools.ListReembedJobs(factory, limit: 20, profile: null, TestContext.Current.CancellationToken);
        var array = JsonNode.Parse(json) as JsonArray;

        Assert.NotNull(array);
        Assert.Single(array);
        var first = array[0] as JsonObject;
        Assert.NotNull(first);
        foreach(var key in ListReembedJobsEntryFields)
            Assert.True(first.ContainsKey(key), $"list_reembed_jobs entry missing '{key}' field");
    }

    [Fact]
    public async Task GetJobStatusShapeContainsAllDocumentedFieldsForAnyJobType()
    {
        (var factory, var jobRepo) = MakeFactory();
        jobRepo.GetAsync("j-1", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<JobRecord?>(JobOfType("j-1", JobType.Rechunk)));

        var json =
            await BackgroundJobTools.GetJobStatus(factory, "j-1", profile: null, TestContext.Current.CancellationToken);
        var root = JsonNode.Parse(json) as JsonObject;

        Assert.NotNull(root);
        foreach(var key in GetJobStatusFields)
            Assert.True(root.ContainsKey(key), $"get_job_status missing '{key}' field");
        Assert.Equal("Rechunk", root["JobType"]?.GetValue<string>());
        Assert.Equal("Completed", root["Status"]?.GetValue<string>());
    }

    [Fact]
    public async Task GetJobStatusReturnsNotFoundMessageWhenJobMissing()
    {
        (var factory, var jobRepo) = MakeFactory();
        jobRepo.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<JobRecord?>(null));

        var result =
            await BackgroundJobTools.GetJobStatus(factory, "missing", profile: null, TestContext.Current.CancellationToken);

        Assert.Contains("No job found", result);
    }

    [Fact]
    public async Task ListJobsShapeIsArrayOfUnifiedJobSummaryObjects()
    {
        (var factory, var jobRepo) = MakeFactory();
        jobRepo.ListRecentAsync(Arg.Any<JobType?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns([JobOfType("a", JobType.Scrape), JobOfType("b", JobType.Rechunk)]);

        var json = await BackgroundJobTools.ListJobs(factory,
                                                     jobType: null,
                                                     limit: 20,
                                                     profile: null,
                                                     TestContext.Current.CancellationToken
                                                    );
        var array = JsonNode.Parse(json) as JsonArray;

        Assert.NotNull(array);
        Assert.Equal(2, array.Count);
        var first = array[0] as JsonObject;
        Assert.NotNull(first);
        foreach(var key in ListJobsEntryFields)
            Assert.True(first.ContainsKey(key), $"list_jobs entry missing '{key}' field");
    }

    private static readonly string[] GetRescrubStatusFields =
        [
            "Id", "Status", "PipelineState", "LibraryId", "Version", "ChunksTotal", "ChunksProcessed",
            "ChunksChanged", "ErrorMessage", "CreatedAt", "StartedAt", "CompletedAt", "LastProgressAt",
            "BoundaryHint", "Result"
        ];

    private static readonly string[] ListRescrubJobsEntryFields =
        [
            "Id", "Status", "PipelineState", "LibraryId", "Version", "ChunksTotal", "ChunksProcessed",
            "ChunksChanged", "CreatedAt", "CompletedAt"
        ];

    private static readonly string[] GetReembedStatusFields =
        [
            "Id", "Status", "PipelineState", "LibraryId", "Version", "ChunksTotal", "ChunksProcessed",
            "ErrorMessage", "CreatedAt", "StartedAt", "CompletedAt", "LastProgressAt", "Result"
        ];

    private static readonly string[] ListReembedJobsEntryFields =
        [
            "Id", "Status", "PipelineState", "LibraryId", "Version", "ChunksTotal", "ChunksProcessed"
        ];

    private static readonly string[] GetJobStatusFields =
        [
            "Id", "JobType", "Status", "PipelineState", "Profile", "LibraryId", "Version",
            "ItemsProcessed", "ItemsTotal", "ItemsLabel", "ErrorMessage", "Result", "BoundaryHint",
            "CreatedAt", "StartedAt", "CompletedAt", "LastProgressAt", "CancelledAt"
        ];

    private static readonly string[] ListJobsEntryFields =
        [
            "Id", "Status", "JobType", "LibraryId", "Version", "ItemsProcessed", "ItemsTotal",
            "ItemsLabel", "CreatedAt", "CompletedAt"
        ];
}
