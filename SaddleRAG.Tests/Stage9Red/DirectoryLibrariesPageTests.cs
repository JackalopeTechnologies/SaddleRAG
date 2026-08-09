// Stage 9 acceptance coverage.
// The page follows the repository's TestableXxxPage pattern; no rendering package is added.

using System.Reflection;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Scanning;
using SaddleRAG.Monitor.Pages;
using SaddleRAG.Monitor.Services;

namespace SaddleRAG.Tests.Monitor;

public sealed class DirectoryLibrariesPageTests
{
    [Fact]
    public async Task InitializationLoadsRowsButNeverTestsRegistersOrScans()
    {
        var data = Substitute.For<IDirectoryLibraryMonitorDataService>();
        var commands = Substitute.For<IDirectoryLibraryMonitorCommands>();
        data.ListAsync(null, Arg.Any<CancellationToken>())
            .Returns(new[] { Row() });
        var page = new TestablePage(data, commands);

        await page.InitializeAsync();

        DirectoryLibraryMonitorRow row = Assert.Single(page.RowsForTest);
        Assert.Equal(RootPath, row.RootPath);
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await commands.DidNotReceiveWithAnyArgs().TestAccessAsync(default!, default, testToken);
        await commands.DidNotReceiveWithAnyArgs().RegisterAsync(default!, default, testToken);
        await commands.DidNotReceiveWithAnyArgs().ScanAsync(default!, default, testToken);
    }

    [Fact]
    public async Task EditedRootIsVisibleAndOnlyExplicitButtonsInvokeCommands()
    {
        var data = Substitute.For<IDirectoryLibraryMonitorDataService>();
        var commands = Substitute.For<IDirectoryLibraryMonitorCommands>();
        data.ListAsync(null, Arg.Any<CancellationToken>()).Returns(new[] { Row() });
        commands.TestAccessAsync(EditedRoot, true, Arg.Any<CancellationToken>())
                .Returns(new DirectoryAccessTestResult(true, "DIRECTORY_ACCESS_OK", "Directory is readable."));
        commands.RegisterAsync(Arg.Any<DirectoryRegistrationRequest>(), null, Arg.Any<CancellationToken>())
                .Returns(new DirectoryRegistrationResult("REGISTERED", LibraryId));
        commands.ScanAsync(LibraryId, null, Arg.Any<CancellationToken>())
                .Returns(new DirectoryScanQueueResult("QUEUED", LibraryId, "2026-08-04", "job-9"));
        var page = new TestablePage(data, commands);
        await page.InitializeAsync();

        page.EditRoot(EditedRoot);
        Assert.Equal(EditedRoot, page.RootPathForTest);
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await commands.DidNotReceiveWithAnyArgs().RegisterAsync(default!, default, testToken);
        await commands.DidNotReceiveWithAnyArgs().ScanAsync(default!, default, testToken);

        await page.InvokeTestAccessAsync();
        await commands.Received(1).TestAccessAsync(EditedRoot, true, Arg.Any<CancellationToken>());
        await commands.DidNotReceiveWithAnyArgs().RegisterAsync(default!, default, testToken);

        await page.InvokeRegisterAsync();
        await commands.Received(1)
                      .RegisterAsync(Arg.Is<DirectoryRegistrationRequest>(request =>
                                      MatchesEditedRegistration(request)),
                                     null,
                                     Arg.Any<CancellationToken>());
        await commands.DidNotReceiveWithAnyArgs().ScanAsync(default!, default, testToken);

        await page.InvokeScanAsync();
        await commands.Received(1).ScanAsync(LibraryId, null, Arg.Any<CancellationToken>());
        Assert.Equal("job-9", page.QueuedJobIdForTest);
    }

    [Fact]
    public void RazorSurfaceContainsRequiredManualControlsAndNoScheduler()
    {
        string root = ResolveRepositoryRoot();
        string razor = File.ReadAllText(Path.Combine(root,
                                                     "SaddleRAG.Monitor",
                                                     "Pages",
                                                     "DirectoryLibrariesPage.razor"));

        foreach (string required in new[]
                 {
                     "Directory Libraries", "Root path", "Test access", "Library ID", "Name", "Hint",
                     "Recursive", "Supported extensions", "Register", "Scan", "Last successful",
                     "Progress", "File failures", "Wrap=\"Wrap.Wrap\""
                 })
            Assert.Contains(required, razor, StringComparison.OrdinalIgnoreCase);

        foreach (string prohibited in new[]
                 {
                     "Schedule", "Cron", "Interval", "Watch folder", "Scan on startup", "Auto scan"
                 })
            Assert.DoesNotContain(prohibited, razor, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestablePage : DirectoryLibrariesPageBase
    {
        public TestablePage(IDirectoryLibraryMonitorDataService data,
                            IDirectoryLibraryMonitorCommands commands)
        {
            SetInjected("DataService", data);
            SetInjected("Commands", commands);
        }

        public IReadOnlyList<DirectoryLibraryMonitorRow> RowsForTest => Rows;
        public string RootPathForTest => EditModel.RootPath;
        public string? QueuedJobIdForTest => QueuedJobId;

        public Task InitializeAsync() => OnInitializedAsync();
        public void EditRoot(string path) => EditModel.RootPath = path;
        public Task InvokeTestAccessAsync() => TestAccessAsync();
        public Task InvokeRegisterAsync() => RegisterAsync();
        public Task InvokeScanAsync() => ScanAsync();

        private void SetInjected(string propertyName, object value)
        {
            PropertyInfo? property = typeof(DirectoryLibrariesPageBase)
                                     .GetProperty(propertyName,
                                                  BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(property);
            property.SetValue(this, value);
        }
    }

    private static bool MatchesEditedRegistration(DirectoryRegistrationRequest? request) =>
        request != null
        && request.LibraryId == LibraryId
        && request.RootPath == EditedRoot
        && request.Recursive;

    private static DirectoryLibraryMonitorRow Row() => new()
        {
            LibraryId = LibraryId,
            Name = "Service Manuals",
            Hint = "maintenance and setup",
            RootPath = RootPath,
            Recursive = true,
            AllowedExtensions = [".pdf", ".docx", ".html", ".md", ".txt"],
            LastSuccessfulVersion = "2026-08-04",
            LastSuccessfulAt = new DateTime(2026, 8, 4, 17, 0, 0, DateTimeKind.Utc),
            FileFailures = []
        };

    private static string ResolveRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "SaddleRAG.slnx")))
            current = current.Parent;
        Assert.NotNull(current);
        return current.FullName;
    }

    private const string LibraryId = "service-manuals";
    private const string RootPath = "D:\\User Libraries\\Service Manuals";
    private const string EditedRoot = "E:\\Manual Archive";
}
