// JobRepositoryBuildDeleteFilterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Tests.Database;

/// <summary>
///     Pins the JobRepository.BuildDeleteFilter safety net: an all-null
///     filter set must return <c>null</c>, not a match-everything filter.
///     If this regresses, the cleanup_jobs MCP tool would happily delete
///     every job in the collection on any future invocation that forgets
///     to supply at least one criterion — exactly the disaster the comment
///     on the method calls out.
/// </summary>
public sealed class JobRepositoryBuildDeleteFilterTests
{
    [Fact]
    public void BuildDeleteFilterReturnsNullWhenAllCriteriaAreNull()
    {
        var filter = JobRepository.BuildDeleteFilter(jobType: null,
                                                     status: null,
                                                     libraryId: null,
                                                     version: null,
                                                     completedBefore: null
                                                    );
        Assert.Null(filter);
    }

    [Fact]
    public void BuildDeleteFilterReturnsNullWhenLibraryIdIsEmptyString()
    {
        var filter = JobRepository.BuildDeleteFilter(jobType: null,
                                                     status: null,
                                                     libraryId: string.Empty,
                                                     version: null,
                                                     completedBefore: null
                                                    );
        Assert.Null(filter);
    }

    [Fact]
    public void BuildDeleteFilterReturnsNonNullFilterWhenJobTypeOnlyIsSupplied()
    {
        var filter = JobRepository.BuildDeleteFilter(jobType: JobType.Scrape,
                                                     status: null,
                                                     libraryId: null,
                                                     version: null,
                                                     completedBefore: null
                                                    );
        Assert.NotNull(filter);
    }

    [Fact]
    public void BuildDeleteFilterReturnsNonNullFilterWhenStatusOnlyIsSupplied()
    {
        var filter = JobRepository.BuildDeleteFilter(jobType: null,
                                                     status: JobStatus.Completed,
                                                     libraryId: null,
                                                     version: null,
                                                     completedBefore: null
                                                    );
        Assert.NotNull(filter);
    }

    [Fact]
    public void BuildDeleteFilterReturnsNonNullFilterWhenCompletedBeforeOnlyIsSupplied()
    {
        var filter = JobRepository.BuildDeleteFilter(jobType: null,
                                                     status: null,
                                                     libraryId: null,
                                                     version: null,
                                                     completedBefore: DateTime.UtcNow.AddDays(-7)
                                                    );
        Assert.NotNull(filter);
    }

    [Fact]
    public void BuildDeleteFilterCombinesMultipleCriteriaIntoSingleAndFilter()
    {
        var filter = JobRepository.BuildDeleteFilter(jobType: JobType.Scrape,
                                                     status: JobStatus.Completed,
                                                     libraryId: "lib",
                                                     version: "v1",
                                                     completedBefore: DateTime.UtcNow
                                                    );
        Assert.NotNull(filter);
    }
}
