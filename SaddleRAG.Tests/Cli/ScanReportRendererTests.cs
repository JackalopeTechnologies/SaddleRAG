// ScanReportRendererTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Cli.Handlers;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Tests.Cli;

/// <summary>
///     Pins the saddlerag-cli scan output format. CI scripts and CLI
///     consumers grep for the counter labels ("Resolution failed:",
///     "Queued for scraping", etc.) — a silent rename here would break
///     them.
/// </summary>
public sealed class ScanReportRendererTests
{
    private static PackageIndexStatus Pkg(string id,
                                          string version,
                                          string status,
                                          string? docUrl = null,
                                          string? errorMessage = null) =>
        new PackageIndexStatus
            {
                PackageId = id,
                Version = version,
                EcosystemId = "nuget",
                Status = status,
                DocUrl = docUrl,
                ErrorMessage = errorMessage
            };

    private static DependencyIndexReport NewReport(IReadOnlyList<PackageIndexStatus>? packages = null) =>
        new DependencyIndexReport
            {
                ProjectPath = "/tmp/project.csproj",
                TotalDependencies = packages?.Count ?? 0,
                FilteredOut = 0,
                AlreadyCached = 0,
                CachedDifferentVersion = 0,
                NewlyQueued = 0,
                ResolutionFailed = 0,
                Packages = packages ?? []
            };

    [Fact]
    public void RenderPrintsAllSevenHeaderCounterLines()
    {
        var report = NewReport() with
            {
                TotalDependencies = 10,
                FilteredOut = 1,
                AlreadyCached = 2,
                CachedDifferentVersion = 3,
                NewlyQueued = 4,
                ResolutionFailed = 0
            };
        var output = new StringWriter();

        var exit = ScanReportRenderer.Render(report, output);

        Assert.Equal(0, exit);
        var rendered = output.ToString();
        Assert.Contains("=== Dependency Scan Report ===", rendered);
        Assert.Contains("Project path:", rendered);
        Assert.Contains("Total dependencies found:  10", rendered);
        Assert.Contains("Filtered out:              1", rendered);
        Assert.Contains("Already cached:            2", rendered);
        Assert.Contains("Cached (different version): 3", rendered);
        Assert.Contains("Newly queued:              4", rendered);
        Assert.Contains("Resolution failed:         0", rendered);
    }

    [Fact]
    public void RenderOmitsQueuedSectionWhenNoQueuedPackages()
    {
        var report = NewReport([Pkg("Foo", "1.0", "cached"), Pkg("Bar", "2.0", "filtered")]);
        var output = new StringWriter();

        ScanReportRenderer.Render(report, output);

        Assert.DoesNotContain("Queued for scraping", output.ToString());
    }

    [Fact]
    public void RenderPrintsQueuedSectionWithEcosystemPackageVersionAndDocUrl()
    {
        var report = NewReport([Pkg("Newtonsoft.Json", "13.0.3", "queued", docUrl: "https://docs.example/njson")]);
        var output = new StringWriter();

        ScanReportRenderer.Render(report, output);

        var rendered = output.ToString();
        Assert.Contains("Queued for scraping (1):", rendered);
        Assert.Contains("nuget/Newtonsoft.Json 13.0.3 -> https://docs.example/njson", rendered);
    }

    [Fact]
    public void RenderPrintsFailedSectionWithErrorMessage()
    {
        var report = NewReport([Pkg("BadPkg", "9.9.9", "failed", errorMessage: "resolution timed out")]);
        var output = new StringWriter();

        ScanReportRenderer.Render(report, output);

        var rendered = output.ToString();
        Assert.Contains("Failed (1):", rendered);
        Assert.Contains("nuget/BadPkg 9.9.9 — resolution timed out", rendered);
    }
}
