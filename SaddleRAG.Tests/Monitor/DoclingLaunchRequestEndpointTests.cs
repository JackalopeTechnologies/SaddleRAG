// DoclingLaunchRequestEndpointTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Ingestion.Documents.Docling;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class DoclingLaunchRequestEndpointTests
{
    private sealed class FakeCapabilityService : IDoclingCapabilityService
    {
        public FakeCapabilityService(DoclingCapabilityState state)
        {
            CurrentStatus = new DoclingCapabilityStatus(state,
                                                        state == DoclingCapabilityState.Ready
                                                            ? "DOCLING_READY"
                                                            : "DOCLING_UNREACHABLE",
                                                        Detail: "test",
                                                        Endpoint: "http://localhost:5001",
                                                        DateTimeOffset.UnixEpoch,
                                                        Remediation: "test");
        }

        public DoclingCapabilityStatus CurrentStatus { get; }
        public int RefreshCalls { get; private set; }

        public Task<DoclingCapabilityStatus> GetStatusAsync(bool refresh = false,
                                                            CancellationToken cancellationToken = default)
        {
            if (refresh)
                RefreshCalls++;

            return Task.FromResult(CurrentStatus);
        }

        public void RecordUnexpectedFailure()
        {
        }
    }

    private sealed class FakeDirectoryLibraryData : IDirectoryLibraryMonitorDataService
    {
        public List<string> LibraryIds { get; } = [];

        public Task<IReadOnlyList<DirectoryLibraryMonitorRow>> ListAsync(string? profile,
                                                                         CancellationToken ct = default)
        {
            IReadOnlyList<DirectoryLibraryMonitorRow> rows = LibraryIds
                .Select(id => new DirectoryLibraryMonitorRow
                              {
                                  LibraryId = id,
                                  Name = id,
                                  Hint = string.Empty,
                                  RootPath = @"C:\docs"
                              })
                .ToList();
            return Task.FromResult(rows);
        }
    }

    private static FakeJobRepository JobsWithActiveScan(string libraryId)
    {
        FakeJobRepository jobs = new();
        jobs.Add(new JobRecord
                 {
                     Id = $"job-{libraryId}",
                     LibraryId = libraryId,
                     JobType = JobType.DirectoryScan,
                     Status = JobStatus.Running
                 });
        return jobs;
    }

    [Fact]
    public async Task LaunchIsRequestedWhenAScanIsActiveAndDoclingIsNotReady()
    {
        FakeDirectoryLibraryData libraries = new();
        libraries.LibraryIds.Add("toshiba");
        DoclingLaunchRequestService service = new(libraries,
                                                  JobsWithActiveScan("toshiba"),
                                                  new FakeCapabilityService(DoclingCapabilityState.Unavailable));

        DoclingLaunchRequestStatus status = await service.GetAsync(profile: null,
                                                                   TestContext.Current.CancellationToken);

        Assert.True(status.LaunchRequested);
        Assert.Equal("DOCLING_UNREACHABLE", status.ReasonCode);
    }

    [Fact]
    public async Task LaunchIsNotRequestedWhenDoclingIsAlreadyReady()
    {
        FakeDirectoryLibraryData libraries = new();
        libraries.LibraryIds.Add("toshiba");
        DoclingLaunchRequestService service = new(libraries,
                                                  JobsWithActiveScan("toshiba"),
                                                  new FakeCapabilityService(DoclingCapabilityState.Ready));

        DoclingLaunchRequestStatus status = await service.GetAsync(profile: null,
                                                                   TestContext.Current.CancellationToken);

        Assert.False(status.LaunchRequested);
    }

    [Fact]
    public async Task LaunchIsNotRequestedWhenNoDirectoryLibraryIsRegistered()
    {
        DoclingLaunchRequestService service = new(new FakeDirectoryLibraryData(),
                                                  new FakeJobRepository(),
                                                  new FakeCapabilityService(DoclingCapabilityState.Unavailable));

        DoclingLaunchRequestStatus status = await service.GetAsync(profile: null,
                                                                   TestContext.Current.CancellationToken);

        Assert.False(status.LaunchRequested);
    }

    [Fact]
    public async Task LaunchIsNotRequestedWhenNoScanIsWaiting()
    {
        FakeDirectoryLibraryData libraries = new();
        libraries.LibraryIds.Add("toshiba");
        DoclingLaunchRequestService service = new(libraries,
                                                  new FakeJobRepository(),
                                                  new FakeCapabilityService(DoclingCapabilityState.Unavailable));

        DoclingLaunchRequestStatus status = await service.GetAsync(profile: null,
                                                                   TestContext.Current.CancellationToken);

        Assert.False(status.LaunchRequested);
    }

    [Fact]
    public async Task ThePollNeverForcesACapabilityRefresh()
    {
        FakeDirectoryLibraryData libraries = new();
        libraries.LibraryIds.Add("toshiba");
        FakeCapabilityService capability = new(DoclingCapabilityState.Unavailable);
        DoclingLaunchRequestService service = new(libraries, JobsWithActiveScan("toshiba"), capability);

        await service.GetAsync(profile: null, TestContext.Current.CancellationToken);

        Assert.Equal(expected: 0, capability.RefreshCalls);
    }
}
