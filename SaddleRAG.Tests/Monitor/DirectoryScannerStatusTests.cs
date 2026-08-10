// DirectoryScannerStatusTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Reflection;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Documents.Docling;
using SaddleRAG.Ingestion.Scanning;
using SaddleRAG.Monitor.Pages;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

/// <summary>
///     The Directories page has to say what the document scanner is doing and refuse a
///     scan it already knows will be rejected, rather than letting the press fail later.
/// </summary>
public sealed class DirectoryScannerStatusTests
{
    [Fact]
    public async Task PageTrustsAReadyCachedStatusAndDoesNotProbeOnLoad()
    {
        var capability = Substitute.For<IDoclingCapabilityService>();
        capability.CurrentStatus.Returns(Ready());
        TestablePage page = Page(capability, Row());

        await page.InitializeAsync();

        Assert.Equal(DoclingCapabilityState.Ready, page.ScannerStatusForTest.State);
        Assert.False(page.ScanBlockedForTest);
        await capability.DidNotReceiveWithAnyArgs()
                        .GetStatusAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PageReprobesANotReadyCachedStatusSoAStaleFailureCannotStickTheScanButton()
    {
        // The capability service keeps a recorded failure until a refresh clears it, so a
        // scanner that died and was restarted still reads Unavailable from cache. Blocking
        // on that value alone left Scan permanently grey with no hint beyond one button.
        var capability = Substitute.For<IDoclingCapabilityService>();
        capability.CurrentStatus.Returns(Unavailable(DoclingReasonCodes.EndpointUnreachable,
                                                     "connection refused"));
        capability.GetStatusAsync(true, Arg.Any<CancellationToken>()).Returns(Ready());
        TestablePage page = Page(capability, Row());

        await page.InitializeAsync();

        await capability.Received(1).GetStatusAsync(true, Arg.Any<CancellationToken>());
        Assert.Equal(DoclingCapabilityState.Ready, page.ScannerStatusForTest.State);
        Assert.False(page.ScanBlockedForTest);
    }

    [Fact]
    public async Task PageKeepsTheObservedFailureWhenTheReprobeAlsoFails()
    {
        var capability = Substitute.For<IDoclingCapabilityService>();
        capability.CurrentStatus.Returns(Unavailable(DoclingReasonCodes.EndpointUnreachable, "stale"));
        capability.GetStatusAsync(true, Arg.Any<CancellationToken>())
                  .Returns(Unavailable(DoclingReasonCodes.HealthTimeout, "connection refused"));
        TestablePage page = Page(capability, Row());

        await page.InitializeAsync();

        Assert.Equal(DoclingReasonCodes.HealthTimeout, page.ScannerStatusForTest.ReasonCode);
        Assert.Equal("connection refused", page.ScannerStatusForTest.Detail);
        Assert.True(page.ScanBlockedForTest);
    }

    [Fact]
    public async Task ScanIsRefusedWhileTheScannerIsDownForALibraryThatAcceptsDocumentFormats()
    {
        IDoclingCapabilityService capability =
            PersistentlyUnavailable(DoclingReasonCodes.ModelsUnavailable,
                                    "Docling responded, but its models are unavailable.");
        var commands = Substitute.For<IDirectoryLibraryMonitorCommands>();
        TestablePage page = Page(capability, Row(), commands);
        await page.InitializeAsync();

        Assert.True(page.ScanBlockedForTest);

        await page.InvokeScanAsync();

        await commands.DidNotReceiveWithAnyArgs()
                      .ScanAsync(default!, default, TestContext.Current.CancellationToken);
        Assert.NotNull(page.FailureMessageForTest);
        Assert.Contains(DoclingReasonCodes.ModelsUnavailable,
                        page.FailureMessageForTest!,
                        StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanIsAllowedWhileTheScannerIsDownForALibraryWithNoDocumentFormats()
    {
        IDoclingCapabilityService capability =
            PersistentlyUnavailable(DoclingReasonCodes.EndpointUnreachable, "refused");
        var commands = Substitute.For<IDirectoryLibraryMonitorCommands>();
        commands.ScanAsync(LibraryId, null, Arg.Any<CancellationToken>())
                .Returns(new DirectoryScanQueueResult(DirectoryScanQueueStatuses.Queued,
                                                       LibraryId,
                                                       "2026-08-10",
                                                       "job-1"));
        TestablePage page = Page(capability, Row([".md", ".txt", ".html"]), commands);
        await page.InitializeAsync();

        Assert.False(page.ScanBlockedForTest);

        await page.InvokeScanAsync();

        await commands.Received(1).ScanAsync(LibraryId, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanIsAllowedWhenTheScannerIsReady()
    {
        var capability = Substitute.For<IDoclingCapabilityService>();
        capability.CurrentStatus.Returns(Ready());
        var commands = Substitute.For<IDirectoryLibraryMonitorCommands>();
        commands.ScanAsync(LibraryId, null, Arg.Any<CancellationToken>())
                .Returns(new DirectoryScanQueueResult(DirectoryScanQueueStatuses.Queued,
                                                       LibraryId,
                                                       "2026-08-10",
                                                       "job-2"));
        TestablePage page = Page(capability, Row(), commands);
        await page.InitializeAsync();

        Assert.False(page.ScanBlockedForTest);

        await page.InvokeScanAsync();

        await commands.Received(1).ScanAsync(LibraryId, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecheckingTheScannerForcesAProbeAndAdoptsTheNewStatus()
    {
        // Starts Ready so loading the page does not probe; the only probe in this test is
        // the operator's, and it comes back with a different answer that has to be adopted.
        var capability = Substitute.For<IDoclingCapabilityService>();
        capability.CurrentStatus.Returns(Ready());
        capability.GetStatusAsync(true, Arg.Any<CancellationToken>())
                  .Returns(Unavailable(DoclingReasonCodes.EndpointUnreachable, "refused"));
        TestablePage page = Page(capability, Row());
        await page.InitializeAsync();
        Assert.False(page.ScanBlockedForTest);

        await page.InvokeRecheckScannerAsync();

        await capability.Received(1).GetStatusAsync(true, Arg.Any<CancellationToken>());
        Assert.Equal(DoclingReasonCodes.EndpointUnreachable, page.ScannerStatusForTest.ReasonCode);
        Assert.True(page.ScanBlockedForTest);
    }

    [Fact]
    public async Task ARefusedScanReportsTheObservedReasonAndOfficialRecoveryLinks()
    {
        var capability = Substitute.For<IDoclingCapabilityService>();
        capability.CurrentStatus.Returns(Ready());
        var commands = Substitute.For<IDirectoryLibraryMonitorCommands>();
        commands.ScanAsync(LibraryId, null, Arg.Any<CancellationToken>())
                .Returns(new DirectoryScanQueueResult(DirectoryScanQueueStatuses.Failed,
                                                       LibraryId,
                                                       string.Empty,
                                                       JobId: null,
                                                       DoclingReasonCodes.ModelsUnavailable,
                                                       "Docling responded, but its models are unavailable.",
                                                       DoclingInstallInstructions.OfficialInstallUrl,
                                                       DoclingInstallInstructions.OfficialReleaseUrl));
        TestablePage page = Page(capability, Row(), commands);
        await page.InitializeAsync();

        await page.InvokeScanAsync();

        Assert.NotNull(page.FailureMessageForTest);
        Assert.Contains(DoclingReasonCodes.ModelsUnavailable,
                        page.FailureMessageForTest!,
                        StringComparison.Ordinal);
        Assert.Contains("models are unavailable", page.FailureMessageForTest!, StringComparison.Ordinal);
        Assert.Equal(DoclingInstallInstructions.OfficialInstallUrl, page.ScanRecoveryInstallUrlForTest);
        Assert.Equal(DoclingInstallInstructions.OfficialReleaseUrl, page.ScanRecoveryReleaseUrlForTest);
    }

    [Fact]
    public void RazorSurfaceShowsScannerStatusAndTheFileBeingConverted()
    {
        string razor = File.ReadAllText(Path.Combine(ResolveRepositoryRoot(),
                                                     "SaddleRAG.Monitor",
                                                     "Pages",
                                                     "DirectoryLibrariesPage.razor"));

        foreach(string required in new[]
                {
                    "Document scanner", "@ScannerStatus.ReasonCode", "@ScannerStatus.Detail",
                    "@ScannerStatus.Endpoint", "Re-check", "ScanBlocked", "CurrentRelativePath"
                })
            Assert.Contains(required, razor, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A scanner that is down and stays down: the page reprobes any non-Ready cached
    ///     status on load, so a fixture that only stubs the cache would have the reprobe
    ///     answer with a null status rather than the failure under test.
    /// </summary>
    private static IDoclingCapabilityService PersistentlyUnavailable(string reasonCode, string detail)
    {
        var capability = Substitute.For<IDoclingCapabilityService>();
        DoclingCapabilityStatus status = Unavailable(reasonCode, detail);
        capability.CurrentStatus.Returns(status);
        capability.GetStatusAsync(true, Arg.Any<CancellationToken>()).Returns(status);
        return capability;
    }

    private static TestablePage Page(IDoclingCapabilityService capability,
                                      DirectoryLibraryMonitorRow row,
                                      IDirectoryLibraryMonitorCommands? commands = null)
    {
        var data = Substitute.For<IDirectoryLibraryMonitorDataService>();
        data.ListAsync(null, Arg.Any<CancellationToken>()).Returns(new[] { row });
        return new TestablePage(data,
                                 commands ?? Substitute.For<IDirectoryLibraryMonitorCommands>(),
                                 capability);
    }

    private sealed class TestablePage : DirectoryLibrariesPageBase
    {
        public TestablePage(IDirectoryLibraryMonitorDataService data,
                            IDirectoryLibraryMonitorCommands commands,
                            IDoclingCapabilityService capability)
        {
            SetInjected("DataService", data);
            SetInjected("Commands", commands);
            SetInjected("Capability", capability);
        }

        public DoclingCapabilityStatus ScannerStatusForTest => ScannerStatus;
        public bool ScanBlockedForTest => ScanBlocked;
        public string? FailureMessageForTest => FailureMessage;
        public string? ScanRecoveryInstallUrlForTest => ScanRecoveryInstallUrl;
        public string? ScanRecoveryReleaseUrlForTest => ScanRecoveryReleaseUrl;

        public Task InitializeAsync() => OnInitializedAsync();
        public Task InvokeScanAsync() => ScanAsync();
        public Task InvokeRecheckScannerAsync() => RecheckScannerAsync();

        private void SetInjected(string propertyName, object value)
        {
            PropertyInfo? property = typeof(DirectoryLibrariesPageBase)
                                     .GetProperty(propertyName,
                                                  BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(property);
            property.SetValue(this, value);
        }
    }

    private static DirectoryLibraryMonitorRow Row(IReadOnlyList<string>? allowedExtensions = null) => new()
        {
            LibraryId = LibraryId,
            Name = "Service Manuals",
            Hint = "maintenance and setup",
            RootPath = "D:\\User Libraries\\Service Manuals",
            Recursive = true,
            AllowedExtensions = allowedExtensions ?? [".pdf", ".docx", ".html", ".md", ".txt"],
            FileFailures = []
        };

    private static DoclingCapabilityStatus Ready() =>
        new(DoclingCapabilityState.Ready,
            DoclingReasonCodes.Ready,
            "Docling is ready.",
            "http://localhost:5001",
            DateTimeOffset.UtcNow,
            string.Empty);

    private static DoclingCapabilityStatus Unavailable(string reasonCode, string detail) =>
        new(DoclingCapabilityState.Unavailable,
            reasonCode,
            detail,
            "http://localhost:5001",
            DateTimeOffset.UtcNow,
            "Install or repair the user-managed Docling endpoint, then test it again.");

    private static string ResolveRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "SaddleRAG.slnx")))
            current = current.Parent;
        Assert.NotNull(current);
        return current.FullName;
    }

    private const string LibraryId = "service-manuals";
}
