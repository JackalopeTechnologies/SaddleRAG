// CancellationToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class CancellationToolsTests
{
    [Fact]
    public async Task CancelJobSignalledReturnsSignalledOutcome()
    {
        var service = MakeServiceSubstitute();
        service.CancelAsync("abc", Arg.Any<string?>(), Arg.Any<CancellationToken>())
               .Returns(CancelScrapeOutcome.Signalled);

        var json = await CancellationTools.CancelJob(service,
                                                     "abc",
                                                     profile: null,
                                                     TestContext.Current.CancellationToken
                                                    );

        Assert.Contains("\"Outcome\": \"Signalled\"", json);
    }

    [Fact]
    public async Task CancelJobNotFoundReturnsNotFoundOutcome()
    {
        var service = MakeServiceSubstitute();
        service.CancelAsync("missing", Arg.Any<string?>(), Arg.Any<CancellationToken>())
               .Returns(CancelScrapeOutcome.NotFound);

        var json = await CancellationTools.CancelJob(service,
                                                     "missing",
                                                     profile: null,
                                                     TestContext.Current.CancellationToken
                                                    );

        Assert.Contains("\"Outcome\": \"NotFound\"", json);
    }

    [Fact]
    public async Task CancelJobOrphanCleanedUpReturnsOrphanOutcome()
    {
        var service = MakeServiceSubstitute();
        service.CancelAsync("orphan", Arg.Any<string?>(), Arg.Any<CancellationToken>())
               .Returns(CancelScrapeOutcome.OrphanCleanedUp);

        var json = await CancellationTools.CancelJob(service,
                                                     "orphan",
                                                     profile: null,
                                                     TestContext.Current.CancellationToken
                                                    );

        Assert.Contains("\"Outcome\": \"OrphanCleanedUp\"", json);
    }

    [Fact]
    public async Task CancelJobAlreadyTerminalReturnsTerminalOutcome()
    {
        var service = MakeServiceSubstitute();
        service.CancelAsync("done", Arg.Any<string?>(), Arg.Any<CancellationToken>())
               .Returns(CancelScrapeOutcome.AlreadyTerminal);

        var json = await CancellationTools.CancelJob(service,
                                                     "done",
                                                     profile: null,
                                                     TestContext.Current.CancellationToken
                                                    );

        Assert.Contains("\"Outcome\": \"AlreadyTerminal\"", json);
    }

    [Fact]
    public async Task CancelJobNotCancellableReturnsRefusalOutcome()
    {
        var service = MakeServiceSubstitute();
        service.CancelAsync("delete-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
               .Returns(CancelScrapeOutcome.NotCancellable);

        var json = await CancellationTools.CancelJob(service,
                                                     "delete-1",
                                                     profile: null,
                                                     TestContext.Current.CancellationToken
                                                    );

        Assert.Contains("\"Outcome\": \"NotCancellable\"", json);
    }

    private static JobCancellationService MakeServiceSubstitute() =>
        Substitute.ForPartsOf<JobCancellationService>(Substitute.For<IJobCancellationRegistry>(),
                                                       Substitute.For<RepositoryFactory>([null])
                                                      );
}
