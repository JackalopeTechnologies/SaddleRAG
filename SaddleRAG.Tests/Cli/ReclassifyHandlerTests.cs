// ReclassifyHandlerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using NSubstitute;
using SaddleRAG.Cli.Handlers;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;

#endregion

namespace SaddleRAG.Tests.Cli;

/// <summary>
///     Pins the saddlerag-cli reclassify command's iteration behavior.
///     Covers: single-library vs all-libraries scope, the unclassified-vs-
///     all-pages filter, the upsert-only-when-confidence-and-changed
///     contract, and the not-found-throws path. Output is captured against
///     a StringWriter so the "Done. Processed X pages, reclassified Y."
///     summary line is verifiable.
/// </summary>
public sealed class ReclassifyHandlerTests
{
    private static LibraryRecord Library(string id, string version = "v1") =>
        new LibraryRecord
            {
                Id = id,
                Name = id,
                Hint = $"{id} hint",
                CurrentVersion = version,
                AllVersions = [version]
            };

    private static PageRecord Page(string url, DocCategory category) =>
        new PageRecord
            {
                Id = url,
                LibraryId = "lib",
                Version = "v1",
                Url = url,
                Title = url,
                Category = category,
                RawContent = "c",
                FetchedAt = DateTime.UtcNow,
                ContentHash = "h"
            };

    private static ILlmClassifier ClassifierReturning((DocCategory Category, float Confidence) verdict)
    {
        var classifier = Substitute.For<ILlmClassifier>();
        classifier.ClassifyAsync(Arg.Any<PageRecord>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult(verdict));
        return classifier;
    }

    [Fact]
    public async Task RunAsyncThrowsWhenLibraryIdSuppliedAndNotFound()
    {
        var libRepo = Substitute.For<ILibraryRepository>();
        libRepo.GetLibraryAsync("missing", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryRecord?>(null));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                         ReclassifyHandler.RunAsync(libraryId: "missing",
                                                                              allPages: false,
                                                                              libRepo,
                                                                              Substitute.For<IPageRepository>(),
                                                                              Substitute.For<IChunkRepository>(),
                                                                              ClassifierReturning((DocCategory
                                                                                                       .Unclassified,
                                                                                                   0f)
                                                                                                 ),
                                                                              new StringWriter(),
                                                                              TestContext.Current.CancellationToken
                                                                             )
                                                                    );

        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public async Task RunAsyncWithNullLibraryIdIteratesEveryLibrary()
    {
        var libRepo = Substitute.For<ILibraryRepository>();
        libRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<LibraryRecord>>([Library("alpha"), Library("beta")]));
        var pageRepo = Substitute.For<IPageRepository>();
        pageRepo.GetPagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PageRecord>>([]));
        var output = new StringWriter();

        await ReclassifyHandler.RunAsync(libraryId: null,
                                         allPages: false,
                                         libRepo,
                                         pageRepo,
                                         Substitute.For<IChunkRepository>(),
                                         ClassifierReturning((DocCategory.Unclassified, 0f)),
                                         output,
                                         TestContext.Current.CancellationToken
                                        );

        var rendered = output.ToString();
        Assert.Contains("Reclassifying alpha", rendered);
        Assert.Contains("Reclassifying beta", rendered);
    }

    [Fact]
    public async Task RunAsyncWithAllPagesFalseSkipsAlreadyClassifiedPages()
    {
        var libRepo = Substitute.For<ILibraryRepository>();
        libRepo.GetLibraryAsync("lib", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryRecord?>(Library("lib")));
        var pageRepo = Substitute.For<IPageRepository>();
        pageRepo.GetPagesAsync("lib", "v1", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PageRecord>>(
                             [
                                 Page("a", DocCategory.HowTo),
                                 Page("b", DocCategory.Unclassified),
                                 Page("c", DocCategory.ApiReference)
                             ]
                            )
                       );
        var classifier = Substitute.For<ILlmClassifier>();
        classifier.ClassifyAsync(Arg.Any<PageRecord>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult((DocCategory.HowTo, 0.9f)));

        await ReclassifyHandler.RunAsync(libraryId: "lib",
                                         allPages: false,
                                         libRepo,
                                         pageRepo,
                                         Substitute.For<IChunkRepository>(),
                                         classifier,
                                         new StringWriter(),
                                         TestContext.Current.CancellationToken
                                        );

        // Only the Unclassified page should reach the classifier.
        await classifier.Received(1)
                        .ClassifyAsync(Arg.Is<PageRecord>(p => p.Url == "b"),
                                       Arg.Any<string>(),
                                       Arg.Any<CancellationToken>()
                                      );
    }

    [Fact]
    public async Task RunAsyncWithAllPagesTrueClassifiesEveryPage()
    {
        var libRepo = Substitute.For<ILibraryRepository>();
        libRepo.GetLibraryAsync("lib", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryRecord?>(Library("lib")));
        var pageRepo = Substitute.For<IPageRepository>();
        pageRepo.GetPagesAsync("lib", "v1", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PageRecord>>(
                             [
                                 Page("a", DocCategory.HowTo),
                                 Page("b", DocCategory.Unclassified)
                             ]
                            )
                       );
        var classifier = ClassifierReturning((DocCategory.HowTo, 0.9f));

        await ReclassifyHandler.RunAsync(libraryId: "lib",
                                         allPages: true,
                                         libRepo,
                                         pageRepo,
                                         Substitute.For<IChunkRepository>(),
                                         classifier,
                                         new StringWriter(),
                                         TestContext.Current.CancellationToken
                                        );

        await classifier.Received(2)
                        .ClassifyAsync(Arg.Any<PageRecord>(),
                                       Arg.Any<string>(),
                                       Arg.Any<CancellationToken>()
                                      );
    }

    [Fact]
    public async Task RunAsyncSkipsUpsertWhenCategoryUnchangedOrZeroConfidence()
    {
        var libRepo = Substitute.For<ILibraryRepository>();
        libRepo.GetLibraryAsync("lib", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryRecord?>(Library("lib")));
        var pageRepo = Substitute.For<IPageRepository>();
        // First page: same category. Second page: zero confidence.
        pageRepo.GetPagesAsync("lib", "v1", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PageRecord>>(
                             [
                                 Page("a", DocCategory.HowTo),
                                 Page("b", DocCategory.HowTo)
                             ]
                            )
                       );
        var classifier = Substitute.For<ILlmClassifier>();
        classifier
            .ClassifyAsync(Arg.Is<PageRecord>(p => p.Url == "a"), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((DocCategory.HowTo, 0.9f)));
        classifier
            .ClassifyAsync(Arg.Is<PageRecord>(p => p.Url == "b"), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((DocCategory.Sample, 0f)));

        var output = new StringWriter();
        await ReclassifyHandler.RunAsync(libraryId: "lib",
                                         allPages: true,
                                         libRepo,
                                         pageRepo,
                                         Substitute.For<IChunkRepository>(),
                                         classifier,
                                         output,
                                         TestContext.Current.CancellationToken
                                        );

        await pageRepo.DidNotReceiveWithAnyArgs()
                      .UpsertPageAsync(Arg.Any<PageRecord>(), Arg.Any<CancellationToken>());
        Assert.Contains("reclassified 0.", output.ToString());
    }

    [Fact]
    public async Task RunAsyncUpsertsPageAndChunksWhenCategoryChangesAndConfidencePositive()
    {
        var libRepo = Substitute.For<ILibraryRepository>();
        libRepo.GetLibraryAsync("lib", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryRecord?>(Library("lib")));
        var pageRepo = Substitute.For<IPageRepository>();
        pageRepo.GetPagesAsync("lib", "v1", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PageRecord>>([Page("a", DocCategory.Unclassified)]));
        var chunkRepo = Substitute.For<IChunkRepository>();
        var classifier = ClassifierReturning((DocCategory.HowTo, 0.95f));

        var output = new StringWriter();
        await ReclassifyHandler.RunAsync(libraryId: "lib",
                                         allPages: false,
                                         libRepo,
                                         pageRepo,
                                         chunkRepo,
                                         classifier,
                                         output,
                                         TestContext.Current.CancellationToken
                                        );

        await pageRepo.Received(1)
                      .UpsertPageAsync(Arg.Is<PageRecord>(p => p.Category == DocCategory.HowTo),
                                       Arg.Any<CancellationToken>()
                                      );
        await chunkRepo.Received(1)
                       .UpdateCategoryByPageUrlAsync("lib",
                                                     "v1",
                                                     "a",
                                                     DocCategory.HowTo,
                                                     Arg.Any<CancellationToken>()
                                                    );
        Assert.Contains("reclassified 1.", output.ToString());
    }
}
