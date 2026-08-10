// MainLayoutTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core;
using SaddleRAG.Mcp.Monitor;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class MainLayoutTests
{
    private sealed class TestableMainLayout : MainLayoutBase
    {
        public bool DrawerOpenForTest => DrawerOpen;
        public string ServerVersionForTest => ServerVersion;
        public string ServerVersionDetailForTest => ServerVersionDetail;
        public void InvokeToggle() => ToggleDrawer();
    }

    [Fact]
    public void ToggleDrawerFlipsTheDrawerOpenFlag()
    {
        var layout = new TestableMainLayout();
        Assert.True(layout.DrawerOpenForTest);
        layout.InvokeToggle();
        Assert.False(layout.DrawerOpenForTest);
        layout.InvokeToggle();
        Assert.True(layout.DrawerOpenForTest);
    }

    [Fact]
    public void NavigationShellUsesResponsiveDrawerAndHidesDesktopLinksOnSmallScreens()
    {
        string root = ResolveRepositoryRoot();
        string razor = File.ReadAllText(Path.Combine(root,
                                                     "SaddleRAG.Mcp",
                                                     "Monitor",
                                                     "MainLayout.razor"));
        string styles = File.ReadAllText(Path.Combine(root,
                                                      "SaddleRAG.Mcp",
                                                      "Monitor",
                                                      "MainLayout.razor.css"));
        string app = File.ReadAllText(Path.Combine(root,
                                                   "SaddleRAG.Mcp",
                                                   "Monitor",
                                                   "App.razor"));
        string program = File.ReadAllText(Path.Combine(root,
                                                       "SaddleRAG.Mcp",
                                                       "Program.cs"));

        Assert.Contains("Variant=\"DrawerVariant.Responsive\"", razor, StringComparison.Ordinal);
        Assert.Contains("Breakpoint=\"Breakpoint.Md\"", razor, StringComparison.Ordinal);
        Assert.Contains("monitor-top-navigation", razor, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 959.98px)", styles, StringComparison.Ordinal);
        Assert.Contains("display: none", styles, StringComparison.Ordinal);
        Assert.Contains("SaddleRAG.Mcp.styles.css", app, StringComparison.Ordinal);
        Assert.Contains("app.MapStaticAssets();", program, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellShowsTheRunningBuildWithTheFullVersionAvailableForSupport()
    {
        var layout = new TestableMainLayout();

        Assert.Equal(SaddleRagVersion.Display, layout.ServerVersionForTest);
        Assert.Equal(SaddleRagVersion.Informational, layout.ServerVersionDetailForTest);
    }

    [Fact]
    public void ShellRendersTheRunningVersionInTheApplicationBar()
    {
        string razor = File.ReadAllText(Path.Combine(ResolveRepositoryRoot(),
                                                     "SaddleRAG.Mcp",
                                                     "Monitor",
                                                     "MainLayout.razor"));

        Assert.Contains("@ServerVersion", razor, StringComparison.Ordinal);
        Assert.Contains("@ServerVersionDetail", razor, StringComparison.Ordinal);
    }

    private static string ResolveRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "SaddleRAG.slnx")))
            current = current.Parent;
        Assert.NotNull(current);
        return current.FullName;
    }
}
