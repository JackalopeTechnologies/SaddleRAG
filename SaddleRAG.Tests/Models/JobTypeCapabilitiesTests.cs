// JobTypeCapabilitiesTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Tests.Models;

public sealed class JobTypeCapabilitiesTests
{
    [Theory]
    [InlineData(JobType.Scrape)]
    [InlineData(JobType.DryRunScrape)]
    [InlineData(JobType.Rechunk)]
    [InlineData(JobType.Reembed)]
    [InlineData(JobType.Rescrub)]
    public void CancellableTypesReturnTrue(JobType type)
    {
        Assert.True(type.IsCancellable());
    }

    [Theory]
    [InlineData(JobType.RenameLibrary)]
    [InlineData(JobType.RenameVersion)]
    [InlineData(JobType.DeleteVersion)]
    [InlineData(JobType.DeleteLibrary)]
    [InlineData(JobType.IndexProjectDependencies)]
    [InlineData(JobType.SubmitUrlCorrection)]
    [InlineData(JobType.CleanupAuditLog)]
    [InlineData(JobType.CleanupJobs)]
    [InlineData(JobType.CleanupOrphans)]
    [InlineData(JobType.Unknown)]
    public void NonCancellableTypesReturnFalse(JobType type)
    {
        Assert.False(type.IsCancellable());
    }

    [Fact]
    public void EveryEnumValueIsCovered()
    {
        var allTypes = Enum.GetValues<JobType>();
        var cancellable = allTypes.Count(t => t.IsCancellable());
        var nonCancellable = allTypes.Count(t => !t.IsCancellable());

        Assert.Equal(allTypes.Length, cancellable + nonCancellable);
        Assert.Equal(ExpectedCancellableCount, cancellable);
    }

    private const int ExpectedCancellableCount = 5;
}
