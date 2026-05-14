// BackgroundJobRunnerBroadcasterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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
        (var runner, var broadcaster, var _) = MakeRunner();

        var record = MakeRecord(BackgroundJobTypes.RenameLibrary);
        await runner.QueueAsync(record, (_, _, _) => Task.CompletedTask, TestContext.Current.CancellationToken);
        await WaitForCompletion(record, broadcaster);

        broadcaster.Received(requiredNumberOfCalls: 1)
                   .RecordJobStarted(record.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        broadcaster.Received(requiredNumberOfCalls: 1).RecordJobCompleted(record.Id, Arg.Any<int>());
        broadcaster.DidNotReceive().RecordJobFailed(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task CountedJobEmitsProgressForEachOnProgressCall()
    {
        (var runner, var broadcaster, var _) = MakeRunner();

        var record = MakeRecord(BackgroundJobTypes.Rechunk);
        record.ItemsLabel = "chunks";

        await runner.QueueAsync(record,
                                async (_, onProgress, _) =>
                                {
                                    if (onProgress is null)
                                        throw new InvalidOperationException("onProgress must not be null in this test");
                                    onProgress(arg1: 10, arg2: 100);
                                    onProgress(arg1: 20, arg2: 100);
                                    await Task.CompletedTask;
                                },
                                TestContext.Current.CancellationToken
                               );
        await WaitForCompletion(record, broadcaster);

        broadcaster.Received(requiredNumberOfCalls: 1)
                   .RecordJobProgress(record.Id, processed: 10, total: 100, "chunks");
        broadcaster.Received(requiredNumberOfCalls: 1)
                   .RecordJobProgress(record.Id, processed: 20, total: 100, "chunks");
    }

    [Fact]
    public async Task FailedJobEmitsFailedTerminalEvent()
    {
        (var runner, var broadcaster, var _) = MakeRunner();

        var record = MakeRecord(BackgroundJobTypes.DeleteVersion);
        await runner.QueueAsync(record,
                                (_, _, _) => throw new InvalidOperationException("boom"),
                                TestContext.Current.CancellationToken
                               );
        await WaitForCompletion(record, broadcaster);

        broadcaster.Received(requiredNumberOfCalls: 1).RecordJobFailed(record.Id, "boom");
        broadcaster.DidNotReceive().RecordJobCompleted(Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public async Task CancelledJobEmitsCancelledTerminalEvent()
    {
        (var runner, var broadcaster, var _) = MakeRunner();

        var record = MakeRecord(BackgroundJobTypes.Rechunk);
        await runner.QueueAsync(record,
                                (_, _, _) => throw new OperationCanceledException("test cancel"),
                                TestContext.Current.CancellationToken
                               );
        await WaitForCompletion(record, broadcaster);

        broadcaster.Received(requiredNumberOfCalls: 1).RecordJobCancelled(record.Id);
        broadcaster.DidNotReceive().RecordJobCompleted(Arg.Any<string>(), Arg.Any<int>());
        broadcaster.DidNotReceive().RecordJobFailed(Arg.Any<string>(), Arg.Any<string>());
    }

    private static (BackgroundJobRunner runner,
        IMonitorBroadcaster broadcaster,
        FakeJobRepository jobRepo) MakeRunner()
    {
        var jobRepo = new FakeJobRepository();
        var factory = Substitute.For<RepositoryFactory>([null]);
        factory.GetJobRepository(Arg.Any<string?>()).Returns(jobRepo);
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
        var deadline = DateTime.UtcNow.AddSeconds(value: 5);
        while (DateTime.UtcNow < deadline)
        {
            var completed = broadcaster.ReceivedCalls()
                                       .Any(c => c.GetMethodInfo().Name is "RecordJobCompleted"
                                                     or "RecordJobFailed"
                                                     or "RecordJobCancelled"
                                           );
            if (completed)
                return;
            await Task.Delay(millisecondsDelay: 20);
        }

        throw new TimeoutException($"Job {record.Id} did not reach a terminal event within 5s");
    }
}
