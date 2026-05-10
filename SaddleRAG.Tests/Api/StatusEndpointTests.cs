// StatusEndpointTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Mcp.Api;

#endregion

namespace SaddleRAG.Tests.Api;

public class StatusEndpointTests
{
    [Fact]
    public async Task BuildStatusResponseMapsLibrariesAndRunningJobs()
    {
        ILibraryRepository libraries = Substitute.For<ILibraryRepository>();
        IScrapeJobRepository jobs = Substitute.For<IScrapeJobRepository>();

        libraries.GetAllLibrariesAsync(Arg.Any<CancellationToken>())
                 .Returns([
                     new LibraryRecord
                     {
                         Id = "mongodb-driver",
                         Name = "mongodb-driver",
                         Hint = string.Empty,
                         CurrentVersion = "3.1.2",
                         AllVersions = ["3.1.2"]
                     }
                 ]);

        jobs.ListRunningJobsAsync(Arg.Any<CancellationToken>())
            .Returns([
                new ScrapeJobRecord
                {
                    Id = "job-1",
                    Job = new ScrapeJob
                    {
                        LibraryId = "anthropic-sdk",
                        Version = "0.9.0",
                        RootUrl = "https://example.com",
                        LibraryHint = string.Empty,
                        AllowedUrlPatterns = []
                    },
                    Status = ScrapeJobStatus.Running,
                    PipelineState = "Scraping"
                }
            ]);

        StatusResponse result = await StatusApiEndpoints.BuildStatusResponseAsync(libraries, jobs, CancellationToken.None);

        Assert.Single(result.Libraries);
        Assert.Equal("mongodb-driver", result.Libraries[0].Name);
        Assert.Equal("3.1.2", result.Libraries[0].Version);

        Assert.Single(result.ActiveJobs);
        Assert.Equal("job-1", result.ActiveJobs[0].Id);
        Assert.Equal("anthropic-sdk", result.ActiveJobs[0].Library);
        Assert.Equal("Scraping", result.ActiveJobs[0].Phase);
    }

    [Fact]
    public async Task BuildStatusResponseWithNoJobsReturnsEmptyActiveJobs()
    {
        ILibraryRepository libraries = Substitute.For<ILibraryRepository>();
        IScrapeJobRepository jobs = Substitute.For<IScrapeJobRepository>();

        libraries.GetAllLibrariesAsync(Arg.Any<CancellationToken>()).Returns([]);
        jobs.ListRunningJobsAsync(Arg.Any<CancellationToken>()).Returns([]);

        StatusResponse result = await StatusApiEndpoints.BuildStatusResponseAsync(libraries, jobs, CancellationToken.None);

        Assert.Empty(result.Libraries);
        Assert.Empty(result.ActiveJobs);
    }
}
