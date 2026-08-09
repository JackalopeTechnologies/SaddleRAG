// QueuedReembedJobRecoveryTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;

#endregion

namespace SaddleRAG.Tests.Ingestion;

public sealed class QueuedReembedJobRecoveryTests
{
    [Fact]
    public async Task RecoveryScansDefaultAndEveryConfiguredProfileAndPreservesProfile()
    {
        SaddleRagDbContextFactory contexts = ContextFactory();
        var repositories = Substitute.For<RepositoryFactory>([contexts]);
        var defaultJobs = Substitute.For<IJobRepository>();
        var companyJobs = Substitute.For<IJobRepository>();
        var archiveJobs = Substitute.For<IJobRepository>();
        var dispatcher = Substitute.For<IReembedJobDispatcher>();
        JobRecord defaultJob = Job("default-job", profile: null);
        JobRecord companyJob = Job("company-job", CompanyProfile);
        JobRecord mismatched = Job("mismatched-job", profile: null);
        repositories.GetJobRepository(profile: null).Returns(defaultJobs);
        repositories.GetJobRepository(CompanyProfile).Returns(companyJobs);
        repositories.GetJobRepository(ArchiveProfile).Returns(archiveJobs);
        defaultJobs.ListQueuedAsync(JobType.Reembed, profile: null, Arg.Any<CancellationToken>())
                   .Returns([defaultJob]);
        companyJobs.ListQueuedAsync(JobType.Reembed, CompanyProfile, Arg.Any<CancellationToken>())
                   .Returns([companyJob, mismatched]);
        archiveJobs.ListQueuedAsync(JobType.Reembed, ArchiveProfile, Arg.Any<CancellationToken>())
                   .Returns(_ => Task.FromException<IReadOnlyList<JobRecord>>(
                                new IOException("archive unavailable")));
        dispatcher.TryDispatchPersisted(Arg.Any<JobRecord>()).Returns(true);
        var recovery = new QueuedReembedJobRecovery(contexts,
                                                     repositories,
                                                     dispatcher,
                                                     NullLogger<QueuedReembedJobRecovery>.Instance);

        int scheduled = await recovery.RecoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, scheduled);
        dispatcher.Received(requiredNumberOfCalls: 1).TryDispatchPersisted(defaultJob);
        dispatcher.Received(requiredNumberOfCalls: 1).TryDispatchPersisted(companyJob);
        dispatcher.DidNotReceive().TryDispatchPersisted(mismatched);
        await defaultJobs.Received(requiredNumberOfCalls: 1)
                         .ListQueuedAsync(JobType.Reembed,
                                          profile: null,
                                          Arg.Any<CancellationToken>());
        await companyJobs.Received(requiredNumberOfCalls: 1)
                         .ListQueuedAsync(JobType.Reembed,
                                          CompanyProfile,
                                          Arg.Any<CancellationToken>());
        await archiveJobs.Received(requiredNumberOfCalls: 1)
                         .ListQueuedAsync(JobType.Reembed,
                                          ArchiveProfile,
                                          Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoveryPropagatesRequestedCancellation()
    {
        SaddleRagDbContextFactory contexts = ContextFactory();
        var repositories = Substitute.For<RepositoryFactory>([contexts]);
        var dispatcher = Substitute.For<IReembedJobDispatcher>();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var recovery = new QueuedReembedJobRecovery(contexts,
                                                     repositories,
                                                     dispatcher,
                                                     NullLogger<QueuedReembedJobRecovery>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => recovery.RecoverAsync(cancellation.Token));

        repositories.DidNotReceive().GetJobRepository(Arg.Any<string?>());
        dispatcher.DidNotReceive().TryDispatchPersisted(Arg.Any<JobRecord>());
    }

    private static SaddleRagDbContextFactory ContextFactory() => new(
        Options.Create(new SaddleRagDbSettings
                           {
                               BootstrapAllProfilesAtStartup = false,
                               Profiles = new Dictionary<string, MongoDbProfile>(StringComparer.Ordinal)
                                              {
                                                  [CompanyProfile] = new MongoDbProfile(),
                                                  [ArchiveProfile] = new MongoDbProfile()
                                              }
                           }));

    private static JobRecord Job(string id, string? profile) => new()
        {
            Id = id,
            JobType = JobType.Reembed,
            Profile = profile,
            LibraryId = "recovery-library",
            Version = "1.0",
            InputJson = "{}",
            Status = JobStatus.Queued,
            ItemsLabel = "chunks"
        };

    private const string ArchiveProfile = "archive";
    private const string CompanyProfile = "company";
}
