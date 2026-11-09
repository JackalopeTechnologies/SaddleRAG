// LandingPageDatabaseRecoveryTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Reflection;
using NSubstitute.ExceptionExtensions;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Monitor.Pages;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

/// <summary>
///     Pins that the landing page asks for a stopped local MongoDB to be restarted before it
///     reads the library list, on first load and on each retry, and shows the outage message
///     (through its existing "libraries could not be refreshed" banner) instead of reading.
///     Uses the established TestableXxxPage pattern (no bunit).
/// </summary>
public sealed class LandingPageDatabaseRecoveryTests
{
    private sealed class TestableLandingPage : LandingPageBase
    {
        public TestableLandingPage(MonitorDataService data, DatabaseRecovery recovery)
        {
            SetInjected(DataServicePropertyName, data);
            SetInjected(RecoveryPropertyName, recovery);
            SetInjected(ContextFactoryPropertyName, MonitorTestRecovery.ContextFactory());
        }

        public bool UnavailableForTest => LibrariesUnavailable;

        public string? OutageForTest => LibrariesOutageMessage;

        public int LibraryCountForTest => Libraries.Count;

        public Task RefreshForTestAsync() => RefreshLibrariesAsync();

        private void SetInjected(string name, object value)
        {
            var prop = typeof(LandingPageBase).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)
                       ?? throw new InvalidOperationException($"{name} property missing on LandingPageBase");
            prop.SetValue(this, value);
        }

        private const string DataServicePropertyName = "DataService";
        private const string RecoveryPropertyName = "Recovery";
        private const string ContextFactoryPropertyName = "ContextFactory";
    }

    [Fact]
    public async Task FailedRecoveryShowsTheOutageMessageAndDoesNotReadLibraries()
    {
        var libraries = Substitute.For<ILibraryRepository>();
        var page = CreatePage(libraries, isKnownDown: true);

        await page.RefreshForTestAsync();

        Assert.True(page.UnavailableForTest);
        Assert.Contains("unreachable", page.OutageForTest);
        await libraries.DidNotReceiveWithAnyArgs().GetAllLibrariesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SucceedingRecoveryLoadsLibrariesAsBefore()
    {
        var libraries = Substitute.For<ILibraryRepository>();
        libraries.GetAllLibrariesAsync(Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<LibraryRecord>>([]));
        var page = CreatePage(libraries, isKnownDown: false);

        await page.RefreshForTestAsync();

        Assert.False(page.UnavailableForTest);
        Assert.Null(page.OutageForTest);
        await libraries.Received(requiredNumberOfCalls: 1).GetAllLibrariesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadThatFindsTheDatabaseUnreachableShowsItsMessageAndDoesNotThrow()
    {
        var libraries = Substitute.For<ILibraryRepository>();
        libraries.GetAllLibrariesAsync(Arg.Any<CancellationToken>())
                 .ThrowsAsync(new DatabaseUnavailableException(ReadFailureEndpoint, innerException: null));
        var page = CreatePage(libraries, isKnownDown: false);

        await page.RefreshForTestAsync();

        Assert.True(page.UnavailableForTest);
        Assert.Contains(ReadFailureEndpoint, page.OutageForTest);
    }

    [Fact]
    public async Task FailedRecoveryFollowedBySuccessfulRecoveryClearsTheMessage()
    {
        var libraries = Substitute.For<ILibraryRepository>();
        libraries.GetAllLibrariesAsync(Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<LibraryRecord>>([]));
        var recovery = MonitorTestRecovery.Recoverable(out Action recover);
        var page = new TestableLandingPage(CreateData(libraries), recovery);

        await page.RefreshForTestAsync();
        Assert.True(page.UnavailableForTest);
        Assert.NotNull(page.OutageForTest);

        recover();
        await page.RefreshForTestAsync();

        Assert.False(page.UnavailableForTest);
        Assert.Null(page.OutageForTest);
    }

    private static MonitorDataService CreateData(ILibraryRepository libraries) =>
        new MonitorDataService(libraries,
                               Substitute.For<IChunkRepository>(),
                               Substitute.For<ILibraryProfileRepository>(),
                               Substitute.For<IUnifiedJobView>(),
                               Substitute.For<IScrapeAuditRepository>());

    private const string ReadFailureEndpoint = "localhost:27017";

    private static TestableLandingPage CreatePage(ILibraryRepository libraries, bool isKnownDown)
    {
        var data = new MonitorDataService(libraries,
                                          Substitute.For<IChunkRepository>(),
                                          Substitute.For<ILibraryProfileRepository>(),
                                          Substitute.For<IUnifiedJobView>(),
                                          Substitute.For<IScrapeAuditRepository>());
        var recovery = isKnownDown ? MonitorTestRecovery.Failing() : MonitorTestRecovery.Healthy();
        return new TestableLandingPage(data, recovery);
    }
}
