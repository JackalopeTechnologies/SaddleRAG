// HealthPageTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tests.Monitor;

/// <summary>
///     Source-scan tests for the service health page, following the repository's no-bunit
///     pattern for Monitor pages. Each expected line is asserted as a single-line substring.
/// </summary>
public sealed class HealthPageTests
{
    private const string LocalLine =
        "Hosted on this computer. To restart it, open Components in the SaddleRAG tray.";

    private const string RemoteLine = "Hosted on another computer.";

    [Fact]
    public void LocalServiceShowsWhereItRunsAndHowToRestartIt()
    {
        string page = ReadHealthPage();

        Assert.Contains(LocalLine, page, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteServiceSaysItIsHostedOnAnotherComputer()
    {
        string page = ReadHealthPage();

        Assert.Contains(RemoteLine, page, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalityLineIsOnlyShownWhenTheServerReportedIt()
    {
        string page = ReadHealthPage();

        Assert.Contains("service.HostedLocally is true", page, StringComparison.Ordinal);
        Assert.Contains("service.HostedLocally is false", page, StringComparison.Ordinal);
    }

    private static string ReadHealthPage() =>
        File.ReadAllText(Path.Combine(ResolveRepositoryRoot(), "SaddleRAG.Mcp", "Monitor", "HealthPage.razor"));

    private static string ResolveRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "SaddleRAG.slnx")))
            current = current.Parent;
        Assert.NotNull(current);
        return current.FullName;
    }
}
