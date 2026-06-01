// SearchToolsInjectIdentifierMatchesTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Mcp.Tools;
using SaddleRAG.Tests.Monitor;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class SearchToolsInjectIdentifierMatchesTests
{
    [Fact]
    public async Task InjectIdentifierMatchesAsyncPrependsExactCaseInsensitiveMatch()
    {
        var hybrid = new[] { MakeHybridCandidate("existing-1", "Unrelated") };
        var fakeRepo = new FakeChunkRepository();
        var matchChunk = MakeChunk("symbol-chunk", "MovePvt");
        fakeRepo.SetQualifiedNameMatches("MovePVT", [matchChunk]);

        var result = await SearchTools.InjectIdentifierMatchesAsync(hybrid,
                                                                    fakeRepo,
                                                                    "MovePVT",
                                                                    "lib",
                                                                    "1.0",
                                                                    TestContext.Current.CancellationToken
                                                                   );

        Assert.Equal(2, result.Count);
        Assert.Equal("symbol-chunk", result[0].Chunk.Id);
    }

    [Fact]
    public async Task InjectIdentifierMatchesAsyncRejectsSubstringNearMisses()
    {
        var hybrid = new[] { MakeHybridCandidate("existing-1", "Unrelated") };
        var fakeRepo = new FakeChunkRepository();
        var substringNearMiss = MakeChunk("substring-chunk", "MovePvtExtended");
        fakeRepo.SetQualifiedNameMatches("MovePvt", [substringNearMiss]);

        var result = await SearchTools.InjectIdentifierMatchesAsync(hybrid,
                                                                    fakeRepo,
                                                                    "MovePvt",
                                                                    "lib",
                                                                    "1.0",
                                                                    TestContext.Current.CancellationToken
                                                                   );

        Assert.Single(result);
        Assert.Equal("existing-1", result[0].Chunk.Id);
    }

    [Fact]
    public async Task InjectIdentifierMatchesAsyncDedupesChunksAlreadyInHybridPool()
    {
        var hybrid = new[] { MakeHybridCandidate("shared-chunk", "MovePvt") };
        var fakeRepo = new FakeChunkRepository();
        var duplicate = MakeChunk("shared-chunk", "MovePvt");
        fakeRepo.SetQualifiedNameMatches("MovePvt", [duplicate]);

        var result = await SearchTools.InjectIdentifierMatchesAsync(hybrid,
                                                                    fakeRepo,
                                                                    "MovePvt",
                                                                    "lib",
                                                                    "1.0",
                                                                    TestContext.Current.CancellationToken
                                                                   );

        Assert.Single(result);
        Assert.Equal("shared-chunk", result[0].Chunk.Id);
    }

    [Fact]
    public async Task InjectIdentifierMatchesAsyncCapsInjectedCountAtFive()
    {
        var hybrid = new[] { MakeHybridCandidate("hybrid-1", "Unrelated") };
        var fakeRepo = new FakeChunkRepository();
        var many = Enumerable.Range(1, 12)
                             .Select(i => MakeChunk($"injected-{i}", "Disabled"))
                             .ToList();
        fakeRepo.SetQualifiedNameMatches("Disabled", many);

        var result = await SearchTools.InjectIdentifierMatchesAsync(hybrid,
                                                                    fakeRepo,
                                                                    "Disabled",
                                                                    "lib",
                                                                    "1.0",
                                                                    TestContext.Current.CancellationToken
                                                                   );

        // 5 injected (capped) + 1 hybrid = 6 total
        Assert.Equal(6, result.Count);
        Assert.Equal("hybrid-1", result[^1].Chunk.Id);
    }

    [Fact]
    public async Task InjectIdentifierMatchesAsyncProducesZeroScoresForInjectedRowsSoRerankIsTheOnlySignal()
    {
        var hybrid = Array.Empty<object>().Select(_ => MakeHybridCandidate("", "")).ToList();
        var fakeRepo = new FakeChunkRepository();
        fakeRepo.SetQualifiedNameMatches("MovePvt", [MakeChunk("symbol-chunk", "MovePvt")]);

        var result = await SearchTools.InjectIdentifierMatchesAsync(hybrid,
                                                                    fakeRepo,
                                                                    "MovePvt",
                                                                    "lib",
                                                                    "1.0",
                                                                    TestContext.Current.CancellationToken
                                                                   );

        var injected = result.Single();
        Assert.Equal(expected: 0f, injected.VectorScore);
        Assert.Equal(expected: 0.0, injected.Bm25Score);
        Assert.Equal(expected: 0.0, injected.HybridScore);
    }

    [Fact]
    public async Task InjectIdentifierMatchesOrFallbackAsyncReturnsHybridUnchangedWhenRepositoryThrowsNonCancellation()
    {
        var hybrid = new[]
                         {
                             MakeHybridCandidate("hybrid-1", "Unrelated-1"),
                             MakeHybridCandidate("hybrid-2", "Unrelated-2")
                         };
        var fakeRepo = new FakeChunkRepository();
        fakeRepo.ThrowOnFindByQualifiedName((lib, ver, qn) => new InvalidOperationException($"mongo down: {qn}"));
        var logger = Substitute.For<ILogger<SearchTools.SearchToolsLog>>();
        var metrics = Substitute.For<IQueryMetrics>();

        var result =
            await SearchTools.InjectIdentifierMatchesOrFallbackAsync(hybrid,
                                                                    fakeRepo,
                                                                    "MovePvt",
                                                                    "lib",
                                                                    "1.0",
                                                                    logger,
                                                                    metrics,
                                                                    TestContext.Current.CancellationToken
                                                                   );

        Assert.Same(hybrid, result);
        logger.Received(requiredNumberOfCalls: 1)
              .Log(LogLevel.Warning,
                   Arg.Any<EventId>(),
                   Arg.Any<object>(),
                   Arg.Any<InvalidOperationException>(),
                   Arg.Any<Func<object, Exception?, string>>()
                  );
    }

    [Fact]
    public async Task InjectIdentifierMatchesOrFallbackAsyncPropagatesCancellation()
    {
        var hybrid = new[] { MakeHybridCandidate("hybrid-1", "Unrelated") };
        var fakeRepo = new FakeChunkRepository();
        fakeRepo.ThrowOnFindByQualifiedName((lib, ver, qn) => new OperationCanceledException());
        var logger = Substitute.For<ILogger<SearchTools.SearchToolsLog>>();
        var metrics = Substitute.For<IQueryMetrics>();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => SearchTools.InjectIdentifierMatchesOrFallbackAsync(hybrid,
                                                                    fakeRepo,
                                                                    "MovePvt",
                                                                    "lib",
                                                                    "1.0",
                                                                    logger,
                                                                    metrics,
                                                                    TestContext.Current.CancellationToken
                                                                   ));
        logger.DidNotReceive()
              .Log(Arg.Any<LogLevel>(),
                   Arg.Any<EventId>(),
                   Arg.Any<object>(),
                   Arg.Any<Exception?>(),
                   Arg.Any<Func<object, Exception?, string>>()
                  );
        metrics.DidNotReceive()
               .Record(Arg.Any<string>(),
                       Arg.Any<TimeSpan>(),
                       Arg.Any<bool>(),
                       Arg.Any<int?>(),
                       Arg.Any<string?>()
                      );
    }

    [Fact]
    public async Task InjectIdentifierMatchesOrFallbackAsyncRecordsSuccessMetricWithInjectedCount()
    {
        var hybrid = new[] { MakeHybridCandidate("hybrid-1", "Unrelated") };
        var fakeRepo = new FakeChunkRepository();
        fakeRepo.SetQualifiedNameMatches("MovePvt", [MakeChunk("symbol-chunk", "MovePvt")]);
        var logger = Substitute.For<ILogger<SearchTools.SearchToolsLog>>();
        var metrics = Substitute.For<IQueryMetrics>();

        await SearchTools.InjectIdentifierMatchesOrFallbackAsync(hybrid,
                                                                 fakeRepo,
                                                                 "MovePvt",
                                                                 "lib",
                                                                 "1.0",
                                                                 logger,
                                                                 metrics,
                                                                 TestContext.Current.CancellationToken
                                                                );

        metrics.Received(requiredNumberOfCalls: 1)
               .Record(QueryMetricOperations.IdentifierFastPath,
                       Arg.Any<TimeSpan>(),
                       success: true,
                       resultCount: 1,
                       note: "library=lib"
                      );
    }

    [Fact]
    public async Task InjectIdentifierMatchesOrFallbackAsyncRecordsFailureMetricWithExceptionType()
    {
        var hybrid = new[] { MakeHybridCandidate("hybrid-1", "Unrelated") };
        var fakeRepo = new FakeChunkRepository();
        fakeRepo.ThrowOnFindByQualifiedName((lib, ver, qn) => new InvalidOperationException("mongo down"));
        var logger = Substitute.For<ILogger<SearchTools.SearchToolsLog>>();
        var metrics = Substitute.For<IQueryMetrics>();

        await SearchTools.InjectIdentifierMatchesOrFallbackAsync(hybrid,
                                                                 fakeRepo,
                                                                 "MovePvt",
                                                                 "lib",
                                                                 "1.0",
                                                                 logger,
                                                                 metrics,
                                                                 TestContext.Current.CancellationToken
                                                                );

        metrics.Received(requiredNumberOfCalls: 1)
               .Record(QueryMetricOperations.IdentifierFastPath,
                       Arg.Any<TimeSpan>(),
                       success: false,
                       resultCount: null,
                       note: "InvalidOperationException library=lib"
                      );
    }

    private static SearchTools.HybridCandidate MakeHybridCandidate(string id, string qualifiedName) =>
        new SearchTools.HybridCandidate
            {
                Chunk = MakeChunk(id, qualifiedName),
                VectorScore = 0.5f,
                Bm25Score = 0.5,
                HybridScore = 0.5
            };

    private static DocChunk MakeChunk(string id, string qualifiedName) =>
        new DocChunk
            {
                Id = id,
                LibraryId = "lib",
                Version = "1.0",
                PageUrl = "https://example.com",
                PageTitle = "Page",
                Category = DocCategory.ApiReference,
                Content = "content",
                QualifiedName = qualifiedName
            };
}
