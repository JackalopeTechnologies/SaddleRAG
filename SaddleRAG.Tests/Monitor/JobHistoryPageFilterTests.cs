// JobHistoryPageFilterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Reflection;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Monitor.Pages;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

/// <summary>
///     Pins the JobHistoryPage filter passthrough: the protected
///     StatusFilter / TypeFilter / LibraryFilter / LimitChoice properties
///     must reach <see cref="MonitorJobService.ListAsync" /> verbatim, and
///     the string-shaped StatusFilter must parse to the right
///     <see cref="ScrapeJobStatus" /> enum (case-insensitive). Uses the
///     same TestableXxxPage subclass pattern that
///     <see cref="LandingPageActiveJobsTests" /> established — no bunit
///     needed because the testable surface is plain C# logic.
/// </summary>
public sealed class JobHistoryPageFilterTests
{
    private sealed class TestableJobHistoryPage : JobHistoryPageBase
    {
        public TestableJobHistoryPage(MonitorJobService jobs)
        {
            var prop = typeof(JobHistoryPageBase)
                       .GetProperty(JobsPropertyName, BindingFlags.Instance | BindingFlags.NonPublic)
                       ?? throw new InvalidOperationException("Jobs property missing on JobHistoryPageBase");
            prop.SetValue(this, jobs);
        }

        public string? StatusFilterForTest { set => StatusFilter = value; }
        public string? LibraryFilterForTest { set => LibraryFilter = value; }
        public JobType? TypeFilterForTest { set => TypeFilter = value; }
        public int LimitChoiceForTest { set => LimitChoice = value; }
        public IReadOnlyList<MonitorJobService.JobHistoryRow> RowsForTest => Rows;

        public Task LoadForTestAsync() => LoadAsync();

        private const string JobsPropertyName = "Jobs";
    }

    private static IUnifiedJobView ViewReturning(IReadOnlyList<JobRow> rows)
    {
        var view = Substitute.For<IUnifiedJobView>();
        view.ListAsync(Arg.Any<ScrapeJobStatus?>(),
                       Arg.Any<JobType?>(),
                       Arg.Any<string?>(),
                       Arg.Any<int>(),
                       Arg.Any<CancellationToken>()
                      )
            .Returns(Task.FromResult(rows));
        return view;
    }

    [Fact]
    public async Task LoadAsyncParsesStringStatusFilterIntoScrapeJobStatusEnum()
    {
        var view = ViewReturning([]);
        var page = new TestableJobHistoryPage(new MonitorJobService(view)) { StatusFilterForTest = "Running" };

        await page.LoadForTestAsync();

        await view.Received(1)
                  .ListAsync(ScrapeJobStatus.Running,
                             Arg.Any<JobType?>(),
                             Arg.Any<string?>(),
                             Arg.Any<int>(),
                             Arg.Any<CancellationToken>()
                            );
    }

    [Fact]
    public async Task LoadAsyncPassesNullStatusWhenStringFilterIsEmpty()
    {
        var view = ViewReturning([]);
        var page = new TestableJobHistoryPage(new MonitorJobService(view)) { StatusFilterForTest = string.Empty };

        await page.LoadForTestAsync();

        await view.Received(1)
                  .ListAsync(null,
                             Arg.Any<JobType?>(),
                             Arg.Any<string?>(),
                             Arg.Any<int>(),
                             Arg.Any<CancellationToken>()
                            );
    }

    [Fact]
    public async Task LoadAsyncPassesTypeFilterLibraryFilterAndLimitChoiceVerbatim()
    {
        var view = ViewReturning([]);
        var page = new TestableJobHistoryPage(new MonitorJobService(view))
                       {
                           TypeFilterForTest = JobType.Rescrub,
                           LibraryFilterForTest = "alpha",
                           LimitChoiceForTest = 50
                       };

        await page.LoadForTestAsync();

        await view.Received(1)
                  .ListAsync(Arg.Any<ScrapeJobStatus?>(),
                             JobType.Rescrub,
                             "alpha",
                             50,
                             Arg.Any<CancellationToken>()
                            );
    }

    [Fact]
    public async Task LoadAsyncStoresReturnedRowsInProtectedRowsProperty()
    {
        var rows = new List<JobRow>
                       {
                           new()
                               {
                                   JobId = "j1",
                                   Type = JobType.Scrape,
                                   Status = ScrapeJobStatus.Completed,
                                   CreatedAt = DateTime.UtcNow,
                                   ItemsLabel = "pages"
                               }
                       };
        var view = ViewReturning(rows);
        var page = new TestableJobHistoryPage(new MonitorJobService(view));

        await page.LoadForTestAsync();

        var single = Assert.Single(page.RowsForTest);
        Assert.Equal("j1", single.JobId);
    }
}
