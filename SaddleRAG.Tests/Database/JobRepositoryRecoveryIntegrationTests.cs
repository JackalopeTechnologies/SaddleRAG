// JobRepositoryRecoveryIntegrationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Tests.Database;

[Trait("Category", "Integration")]
public sealed class JobRepositoryRecoveryIntegrationTests : IAsyncLifetime
{
    public JobRepositoryRecoveryIntegrationTests()
    {
        mRepository = new JobRepository(mContext);
    }

    private string mDatabaseName = string.Empty;
    private SaddleRagDbContext mContext = new(Options.Create(new SaddleRagDbSettings()));
    private JobRepository mRepository;

    public async ValueTask InitializeAsync()
    {
        mDatabaseName = $"saddlerag-job-recovery-{Guid.NewGuid():N}";
        mContext = new SaddleRagDbContext(Options.Create(new SaddleRagDbSettings
                                                            {
                                                                ConnectionString = TestConnectionString,
                                                                DatabaseName = mDatabaseName
                                                            }));
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        mRepository = new JobRepository(mContext);
    }

    public async ValueTask DisposeAsync()
    {
        await mContext.Database.Client.DropDatabaseAsync(mDatabaseName);
    }

    [Fact]
    public async Task ListQueuedFiltersTypeStateAndExactProfileOldestFirst()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DateTime now = DateTime.UtcNow;
        await mRepository.UpsertAsync(Job("newer", JobType.Reembed, JobStatus.Queued, Profile, now), ct);
        await mRepository.UpsertAsync(Job("older", JobType.Reembed, JobStatus.Queued, Profile,
                                          now.AddMinutes(-1)), ct);
        await mRepository.UpsertAsync(Job("default", JobType.Reembed, JobStatus.Queued, profile: null,
                                          now.AddMinutes(-2)), ct);
        await mRepository.UpsertAsync(Job("running", JobType.Reembed, JobStatus.Running, Profile,
                                          now.AddMinutes(-3)), ct);
        await mRepository.UpsertAsync(Job("other-type", JobType.Rescrub, JobStatus.Queued, Profile,
                                          now.AddMinutes(-4)), ct);

        IReadOnlyList<JobRecord> profiled = await mRepository.ListQueuedAsync(JobType.Reembed,
                                                                               Profile,
                                                                               ct);
        IReadOnlyList<JobRecord> defaultProfile = await mRepository.ListQueuedAsync(JobType.Reembed,
                                                                                     profile: null,
                                                                                     ct);

        Assert.Equal(["older", "newer"], profiled.Select(record => record.Id));
        Assert.Equal("default", Assert.Single(defaultProfile).Id);
    }

    [Fact]
    public async Task ConcurrentClaimsAllowOnlyOneRunnerAndPreserveProfile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        JobRecord queued = Job("claim-race", JobType.Reembed, JobStatus.Queued, Profile, DateTime.UtcNow);
        await mRepository.UpsertAsync(queued, ct);
        DateTime startedAt = DateTime.UtcNow;

        Task<JobRecord?> first = mRepository.TryClaimQueuedAsync(queued.Id,
                                                                 JobType.Reembed,
                                                                 Profile,
                                                                 "claim-first",
                                                                 startedAt,
                                                                 ct);
        Task<JobRecord?> second = mRepository.TryClaimQueuedAsync(queued.Id,
                                                                  JobType.Reembed,
                                                                  Profile,
                                                                  "claim-second",
                                                                  startedAt,
                                                                  ct);
        JobRecord?[] claims = await Task.WhenAll(first, second);

        JobRecord claimed = Assert.Single(claims.OfType<JobRecord>());
        Assert.Equal(JobStatus.Running, claimed.Status);
        Assert.Equal(nameof(JobStatus.Running), claimed.PipelineState);
        Assert.Equal(Profile, claimed.Profile);
        string claimId = Assert.IsType<string>(claimed.ExecutionClaimId);
        Assert.Contains(claimId, new[] { "claim-first", "claim-second" });
        JobRecord stored = Assert.IsType<JobRecord>(await mRepository.GetAsync(queued.Id, ct));
        Assert.Equal(claimed.ExecutionClaimId, stored.ExecutionClaimId);
        Assert.Empty(await mRepository.ListQueuedAsync(JobType.Reembed, Profile, ct));
    }

    private static JobRecord Job(string id,
                                 JobType jobType,
                                 JobStatus status,
                                 string? profile,
                                 DateTime createdAt) => new()
        {
            Id = id,
            JobType = jobType,
            Profile = profile,
            LibraryId = LibraryId,
            Version = Version,
            InputJson = "{}",
            Status = status,
            PipelineState = status.ToString(),
            ItemsLabel = "chunks",
            CreatedAt = createdAt
        };

    private const string LibraryId = "job-recovery-library";
    private const string Profile = "company";
    private const string TestConnectionString = "mongodb://localhost:27017";
    private const string Version = "1.0";
}
