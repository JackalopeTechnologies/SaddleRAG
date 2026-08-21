// DirectoryScanQueuePreflightFaultTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Scanning;

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     The scan-queue capability preflight probes the user-managed Docling
///     endpoint on the request thread. If that probe faults or is interrupted
///     mid-flight, the queue request must return a bounded, reported failure —
///     never an unhandled exception that the HTTP host renders as a raw 500.
/// </summary>
public sealed class DirectoryScanQueuePreflightFaultTests
{
    [Fact]
    public async Task InterruptedPreflightProbeReturnsBoundedQueueFailureInsteadOfThrowing()
    {
        Harness harness = Harness.Create();
        harness.Preflight.EvaluateAsync(Arg.Any<DirectoryLibraryDefinition>(), Arg.Any<CancellationToken>())
               .ThrowsAsync(new OperationCanceledException());

        DirectoryScanQueueResult result = await harness.Runner.QueueAsync(
                                              LibraryId,
                                              profile: null,
                                              TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanQueueStatuses.Failed, result.Status);
        Assert.Equal(DirectoryScanReasonCodes.ScannerPreflightFailed, result.ReasonCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
        Assert.Null(result.JobId);
        await harness.Jobs.DidNotReceiveWithAnyArgs()
                          .UpsertAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FaultedPreflightProbeReturnsBoundedQueueFailureInsteadOfThrowing()
    {
        Harness harness = Harness.Create();
        harness.Preflight.EvaluateAsync(Arg.Any<DirectoryLibraryDefinition>(), Arg.Any<CancellationToken>())
               .ThrowsAsync(new HttpRequestException("the docling readiness probe failed unexpectedly"));

        DirectoryScanQueueResult result = await harness.Runner.QueueAsync(
                                              LibraryId,
                                              profile: null,
                                              TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanQueueStatuses.Failed, result.Status);
        Assert.Equal(DirectoryScanReasonCodes.ScannerPreflightFailed, result.ReasonCode);
        Assert.Null(result.JobId);
        await harness.Jobs.DidNotReceiveWithAnyArgs()
                          .UpsertAsync(default!, TestContext.Current.CancellationToken);
    }

    private sealed class Harness
    {
        private Harness(DirectoryScanJobRunner runner,
                        IDirectoryDocumentCapabilityPreflight preflight,
                        IJobRepository jobs)
        {
            Runner = runner;
            Preflight = preflight;
            Jobs = jobs;
        }

        public DirectoryScanJobRunner Runner { get; }

        public IDirectoryDocumentCapabilityPreflight Preflight { get; }

        public IJobRepository Jobs { get; }

        public static Harness Create()
        {
            var factory = Substitute.For<RepositoryFactory>([null!]);
            var jobs = Substitute.For<IJobRepository>();
            var sources = Substitute.For<ISourceDocumentRepository>();
            var coordinator = Substitute.For<IDirectoryIngestionCoordinator>();
            var preflight = Substitute.For<IDirectoryDocumentCapabilityPreflight>();
            var cancellation = Substitute.For<IJobCancellationRegistry>();
            var lifetime = Substitute.For<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Returns(CancellationToken.None);
            factory.GetSourceDocumentRepository(null).Returns(sources);
            factory.GetJobRepository(null).Returns(jobs);
            sources.GetDirectoryDefinitionAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(Definition());
            var runner = new DirectoryScanJobRunner(coordinator,
                                                    new DirectoryScanVersionProvider(TimeProvider.System),
                                                    factory,
                                                    cancellation,
                                                    lifetime,
                                                    preflight,
                                                    NullLogger<DirectoryScanJobRunner>.Instance);
            return new Harness(runner, preflight, jobs);
        }

        private static DirectoryLibraryDefinition Definition() => new()
            {
                Id = LibraryId,
                RootPath = "C:\\owned-manuals",
                Recursive = true,
                AllowedExtensions = DirectoryScanLimits.SupportedExtensions,
                ExclusionPatterns = [],
                BindingStatus = DirectoryLibraryBindingStatus.Bound,
                RegisteredAtUtc = DateTime.UtcNow
            };
    }

    private const string LibraryId = "manual-library";
}
