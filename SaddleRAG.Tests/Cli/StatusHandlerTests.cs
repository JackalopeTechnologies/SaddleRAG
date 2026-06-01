// StatusHandlerTests.cs
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
///     Verifies the saddlerag-cli status command's per-version rendering.
///     Exercises the not-found path and the multi-version path so the
///     "(library not found)" friendly message + the
///     "  v{ver}: {pages} pages, {chunks} chunks" formatting won't
///     regress.
/// </summary>
public sealed class StatusHandlerTests
{
    [Fact]
    public async Task RunAsyncPrintsFriendlyMessageWhenLibraryNotFound()
    {
        var libRepo = Substitute.For<ILibraryRepository>();
        libRepo.GetLibraryAsync("missing", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryRecord?>(null));
        var output = new StringWriter();

        var exit = await StatusHandler.RunAsync("missing",
                                                libRepo,
                                                Substitute.For<IPageRepository>(),
                                                Substitute.For<IChunkRepository>(),
                                                output,
                                                TestContext.Current.CancellationToken
                                               );

        Assert.Equal(0, exit);
        Assert.Contains("not found", output.ToString());
    }

    [Fact]
    public async Task RunAsyncRendersOneLinePerVersionWithPageAndChunkCounts()
    {
        var libRepo = Substitute.For<ILibraryRepository>();
        libRepo.GetLibraryAsync("lib", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryRecord?>(new LibraryRecord
                                                            {
                                                                Id = "lib",
                                                                Name = "Lib",
                                                                Hint = "lib hint",
                                                                CurrentVersion = "v2",
                                                                AllVersions = ["v1", "v2"]
                                                            }
                                                       )
                       );
        var pageRepo = Substitute.For<IPageRepository>();
        pageRepo.GetPageCountAsync("lib", "v1", Arg.Any<CancellationToken>()).Returns(Task.FromResult(10));
        pageRepo.GetPageCountAsync("lib", "v2", Arg.Any<CancellationToken>()).Returns(Task.FromResult(15));
        var chunkRepo = Substitute.For<IChunkRepository>();
        chunkRepo.GetChunkCountAsync("lib", "v1", Arg.Any<CancellationToken>()).Returns(Task.FromResult(80));
        chunkRepo.GetChunkCountAsync("lib", "v2", Arg.Any<CancellationToken>()).Returns(Task.FromResult(120));
        var output = new StringWriter();

        var exit = await StatusHandler.RunAsync("lib",
                                                libRepo,
                                                pageRepo,
                                                chunkRepo,
                                                output,
                                                TestContext.Current.CancellationToken
                                               );

        Assert.Equal(0, exit);
        var rendered = output.ToString();
        Assert.Contains("v1: 10 pages, 80 chunks", rendered);
        Assert.Contains("v2: 15 pages, 120 chunks", rendered);
    }
}
