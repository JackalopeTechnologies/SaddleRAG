// ListLibrariesHandlerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using NSubstitute;
using SaddleRAG.Cli.Handlers;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Tests.Cli;

/// <summary>
///     Verifies the saddlerag-cli list command's output formatting.
///     Exercises the handler against a StringWriter so the "empty" vs
///     "non-empty" branches are unit-tested without driving the
///     System.CommandLine harness or console output.
/// </summary>
public sealed class ListLibrariesHandlerTests
{
    [Fact]
    public async Task RunAsyncRendersEmptyListMessageWhenNoLibrariesExist()
    {
        var repo = Substitute.For<ILibraryRepository>();
        repo.GetAllLibrariesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<LibraryRecord>>([]));

        var output = new StringWriter();
        var exit = await ListLibrariesHandler.RunAsync(repo, output, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Contains("No libraries", output.ToString());
    }

    [Fact]
    public async Task RunAsyncRendersOneLinePerLibraryWithIdAndCurrentVersionAndAllVersions()
    {
        var repo = Substitute.For<ILibraryRepository>();
        repo.GetAllLibrariesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<LibraryRecord>>(
                         [
                             new LibraryRecord
                                 {
                                     Id = "alpha",
                                     Name = "Alpha Library",
                                     Hint = "alpha lib",
                                     CurrentVersion = "1.0",
                                     AllVersions = ["0.9", "1.0"]
                                 },
                             new LibraryRecord
                                 {
                                     Id = "beta",
                                     Name = "Beta Library",
                                     Hint = "beta lib",
                                     CurrentVersion = "2.0",
                                     AllVersions = ["2.0"]
                                 }
                         ]
                        )
                   );

        var output = new StringWriter();
        var exit = await ListLibrariesHandler.RunAsync(repo, output, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        var rendered = output.ToString();
        Assert.Contains("alpha", rendered);
        Assert.Contains("Alpha Library", rendered);
        Assert.Contains("1.0", rendered);
        Assert.Contains("0.9", rendered);
        Assert.Contains("beta", rendered);
        Assert.Contains("Beta Library", rendered);
        Assert.Contains("2.0", rendered);
    }
}
