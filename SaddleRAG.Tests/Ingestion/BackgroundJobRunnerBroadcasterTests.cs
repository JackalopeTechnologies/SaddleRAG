// BackgroundJobRunnerBroadcasterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Tests.Monitor;

#endregion

namespace SaddleRAG.Tests.Ingestion;

public sealed class BackgroundJobRunnerBroadcasterTests
{
    [Fact]
    public async Task BinaryJobEmitsStartedAndCompleted()
    {
        var (runner, broadcaster, _) = MakeRunner();

        var record = MakeRecord(BackgroundJobTypes.RenameLibrary);
        await runner.QueueAsync(record, (_, _, _) => Task.CompletedTask);
        await WaitForCompletion(record, broadcaster);

        broadcaster.Received(1).RecordJobStarted(record.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        broadcaster.Received(1).RecordJobCompleted(record.Id, Arg.Any<int>());
        broadcaster.DidNotReceive().RecordJobFailed(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task CountedJobEmitsProgressForEachOnProgressCall()
    {
        var (runner, broadcaster, _) = MakeRunner();

        var record = MakeRecord(BackgroundJobTypes.Rechunk);
        record.ItemsLabel = "chunks";

        await runner.QueueAsync(record, async (_, onProgress, _) =>
        {
            if (onProgress is null) throw new InvalidOperationException("onProgress must not be null in this test");
            onProgress(10, 100);
            onProgress(20, 100);
            await Task.CompletedTask;
        });
        await WaitForCompletion(record, broadcaster);

        broadcaster.Received(1).RecordJobProgress(record.Id, 10, 100, "chunks");
        broadcaster.Received(1).RecordJobProgress(record.Id, 20, 100, "chunks");
    }

    [Fact]
    public async Task FailedJobEmitsFailedTerminalEvent()
    {
        var (runner, broadcaster, _) = MakeRunner();

        var record = MakeRecord(BackgroundJobTypes.DeleteVersion);
        await runner.QueueAsync(record, (_, _, _) => throw new InvalidOperationException("boom"));
        await WaitForCompletion(record, broadcaster);

        broadcaster.Received(1).RecordJobFailed(record.Id, "boom");
        broadcaster.DidNotReceive().RecordJobCompleted(Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public async Task CancelledJobEmitsCancelledTerminalEvent()
    {
        var (runner, broadcaster, _) = MakeRunner();

        var record = MakeRecord(BackgroundJobTypes.Rechunk);
        await runner.QueueAsync(record, (_, _, _) => throw new OperationCanceledException("test cancel"));
        await WaitForCompletion(record, broadcaster);

        broadcaster.Received(1).RecordJobCancelled(record.Id);
        broadcaster.DidNotReceive().RecordJobCompleted(Arg.Any<string>(), Arg.Any<int>());
        broadcaster.DidNotReceive().RecordJobFailed(Arg.Any<string>(), Arg.Any<string>());
    }

    private static (BackgroundJobRunner runner,
                    IMonitorBroadcaster broadcaster,
                    FakeBackgroundJobRepository jobRepo) MakeRunner()
    {
        var jobRepo = new FakeBackgroundJobRepository();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });
        factory.GetBackgroundJobRepository(Arg.Any<string?>()).Returns(jobRepo);
        var broadcaster = Substitute.For<IMonitorBroadcaster>();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        var runner = new BackgroundJobRunner(factory, broadcaster, lifetime, NullLogger<BackgroundJobRunner>.Instance);
        return (runner, broadcaster, jobRepo);
    }

    private static BackgroundJobRecord MakeRecord(string jobType) =>
        new BackgroundJobRecord
            {
                Id = Guid.NewGuid().ToString(),
                JobType = jobType,
                LibraryId = "lib",
                Version = "1",
                InputJson = "{}"
            };

    private static async Task WaitForCompletion(BackgroundJobRecord record, IMonitorBroadcaster broadcaster)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var completed = broadcaster.ReceivedCalls().Any(c => c.GetMethodInfo().Name is "RecordJobCompleted"
                                                              or "RecordJobFailed"
                                                              or "RecordJobCancelled");
            if (completed) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Job {record.Id} did not reach a terminal event within 5s");
    }
}
