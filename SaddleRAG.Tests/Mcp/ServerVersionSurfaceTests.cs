// ServerVersionSurfaceTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json;
using SaddleRAG.Core;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

/// <summary>
///     The running build has to be identifiable from every surface an operator or a
///     client model can reach: the MCP initialize instructions, get_dashboard_index,
///     and the Monitor shell (covered in MainLayoutTests).
/// </summary>
public sealed class ServerVersionSurfaceTests
{
    [Fact]
    public void InformationalVersionIsStampedRatherThanTheDotNetDefault()
    {
        Assert.False(string.IsNullOrWhiteSpace(SaddleRagVersion.Informational));
        Assert.NotEqual("1.0.0.0", SaddleRagVersion.Informational);
    }

    [Fact]
    public void DisplayVersionDropsBuildMetadataAndStaysAPrefixOfTheFullVersion()
    {
        Assert.DoesNotContain('+', SaddleRagVersion.Display);
        Assert.StartsWith(SaddleRagVersion.Display, SaddleRagVersion.Informational, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(SaddleRagVersion.Display));
    }

    [Fact]
    public void ServerInstructionsInterpolateTheRunningVersionFromProgramSource()
    {
        string program = File.ReadAllText(Path.Combine(ResolveRepositoryRoot(),
                                                       "SaddleRAG.Mcp",
                                                       "Program.cs"));

        Assert.Contains("SaddleRagServerInstructions = $\"\"\"", program, StringComparison.Ordinal);
        Assert.Contains("{SaddleRagVersion.Informational}", program, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DashboardIndexReportsTheRunningServerVersion()
    {
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var jobRepo = Substitute.For<IJobRepository>();
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);
        factory.GetJobRepository(Arg.Any<string?>()).Returns(jobRepo);
        libraryRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>()).Returns([]);
        jobRepo.ListRecentAsync(Arg.Any<JobType?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        jobRepo.ListRunningAsync(Arg.Any<JobType?>(), Arg.Any<CancellationToken>()).Returns([]);

        string json = await HealthTools.GetDashboardIndex(factory,
                                                          new McpWarmupState(),
                                                          profile: null,
                                                          TestContext.Current.CancellationToken);

        // Asserted on the parsed value rather than the raw text: the serializer escapes the
        // build-metadata separator as a unicode escape, which every client decodes back to '+'.
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(SaddleRagVersion.Informational,
                     document.RootElement.GetProperty("serverVersion").GetString());
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
