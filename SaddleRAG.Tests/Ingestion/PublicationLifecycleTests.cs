// PublicationLifecycleTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Chunking;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Crawling;
using SaddleRAG.Ingestion.Suspect;
using SaddleRAG.Ingestion.Symbols;

namespace SaddleRAG.Tests.Ingestion;

public sealed class PublicationLifecycleTests
{
    [Fact]
    public async Task SuccessfulRunHonorsPublicationOrderAndPublishesExactlyOnce()
    {
        var harness = BuildHarness();

        await harness.Orchestrator.IngestAsync(Job(), ct: TestContext.Current.CancellationToken);

        AssertOrdered(harness.Events,
                      "building",
                      "bm25",
                      "vector-full",
                      "documents-published",
                      "published",
                      "pointer");
        Assert.Equal(1, harness.States.Count(s => s == VersionPublicationState.Published));
        Assert.Equal("v2", harness.ExistingLibrary.CurrentVersion);
    }

    [Fact]
    public async Task ExistingPublishedVersionIsRejectedBeforeCandidateStateIsTouched()
    {
        var harness = BuildHarness(publishedVersionExists: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Orchestrator.IngestAsync(
            Job(), ct: TestContext.Current.CancellationToken));

        Assert.Contains("already Published", exception.Message, StringComparison.Ordinal);
        Assert.Equal("v2", harness.ExistingLibrary.CurrentVersion);
        Assert.Empty(harness.States);
        Assert.Empty(harness.Events);
        Assert.Equal(0, harness.Vector.IndexCalls);
        Assert.Equal(0, harness.Vector.RemoveCalls);
        await harness.LibraryRepository.DidNotReceive()
                     .UpsertVersionAsync(Arg.Any<LibraryVersionRecord>(), Arg.Any<CancellationToken>());
        await harness.PageRepository.DidNotReceive()
                     .GetPagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Shards.DidNotReceive()
                     .ReplaceShardsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<Bm25Shard>>(),
                                         Arg.Any<CancellationToken>());
        await harness.Shards.DidNotReceive()
                     .DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Indexes.DidNotReceive()
                      .UpsertAsync(Arg.Any<LibraryIndex>(), Arg.Any<CancellationToken>());
        await harness.Indexes.DidNotReceive()
                      .DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisteredDirectoryLibraryRejectsWebScrapeBeforeCandidateMutation()
    {
        var harness = BuildHarness(directoryDefinitionExists: true);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Orchestrator.IngestAsync(Job(), ct: TestContext.Current.CancellationToken));

        Assert.Contains("registered for directory ingestion", exception.Message, StringComparison.Ordinal);
        Assert.Empty(harness.States);
        Assert.Empty(harness.Events);
        Assert.Equal(0, harness.Vector.IndexCalls);
        Assert.Equal(0, harness.Vector.RemoveCalls);
        await harness.SourceDocuments.Received(requiredNumberOfCalls: 1)
                     .GetDirectoryDefinitionAsync("lib", Arg.Any<CancellationToken>());
        await harness.LibraryRepository.DidNotReceive()
                     .GetVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.LibraryRepository.DidNotReceive()
                     .UpsertVersionAsync(Arg.Any<LibraryVersionRecord>(), Arg.Any<CancellationToken>());
        await harness.PageRepository.DidNotReceive()
                     .GetPagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.SourceDocuments.DidNotReceive()
                     .DeleteVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisteredDirectoryLibraryRejectsSinglePageBeforeFetchOrRepositoryMutation()
    {
        var harness = BuildHarness(directoryDefinitionExists: true);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Orchestrator.IngestSinglePageAsync("lib",
                                                       "v2",
                                                       "https://example.test/page",
                                                       ct: TestContext.Current.CancellationToken));

        Assert.Contains("registered for directory ingestion", exception.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Events);
        Assert.Equal(0, harness.Vector.IndexCalls);
        Assert.Equal(0, harness.Vector.RemoveCalls);
        await harness.SourceDocuments.Received(requiredNumberOfCalls: 1)
                     .GetDirectoryDefinitionAsync("lib", Arg.Any<CancellationToken>());
        await harness.ChunkRepository.DidNotReceive()
                     .UpsertChunksAsync(Arg.Any<IReadOnlyList<DocChunk>>(), Arg.Any<CancellationToken>());
        await harness.Shards.DidNotReceive()
                     .ReplaceShardsAsync(Arg.Any<string>(),
                                         Arg.Any<string>(),
                                         Arg.Any<IReadOnlyList<Bm25Shard>>(),
                                         Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BusyWebModeLeaseStopsBeforeCandidateOrCrawlerMutation()
    {
        var harness = BuildHarness();
        harness.ModeLeaseManager.TryAcquireAsync(Arg.Any<string?>(),
                                                 "lib",
                                                 LibraryIngestionMode.Web,
                                                 Arg.Any<CancellationToken>())
                                .Returns((ILibraryIngestionModeLease?) null);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Orchestrator.IngestAsync(Job(), ct: TestContext.Current.CancellationToken));

        Assert.Contains("another web-ingestion operation", exception.Message, StringComparison.Ordinal);
        Assert.Empty(harness.States);
        Assert.Empty(harness.Events);
        await harness.SourceDocuments.DidNotReceive()
                     .GetDirectoryDefinitionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.LibraryRepository.DidNotReceive()
                     .GetVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.LibraryRepository.DidNotReceive()
                     .UpsertVersionAsync(Arg.Any<LibraryVersionRecord>(), Arg.Any<CancellationToken>());
        await harness.PageRepository.DidNotReceive()
                     .GetPagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NamedProfileFailureMutatesOnlyRepositoriesFromThatProfile()
    {
        var harness = BuildHarness(FailurePoint.Acquisition);
        var namedLibraries = Substitute.For<ILibraryRepository>();
        var namedPages = Substitute.For<IPageRepository>();
        var namedChunks = Substitute.For<IChunkRepository>();
        var namedProfiles = Substitute.For<ILibraryProfileRepository>();
        var namedIndexes = Substitute.For<ILibraryIndexRepository>();
        var namedShards = Substitute.For<IBm25ShardRepository>();
        var namedSources = Substitute.For<ISourceDocumentRepository>();
        var namedModes = Substitute.For<ILibraryIngestionModeRepository>();
        namedPages.GetPagesAsync("lib", "v2", Arg.Any<CancellationToken>())
                  .Returns(Array.Empty<PageRecord>());
        namedModes.GetLibraryDataEvidenceAsync("lib", Arg.Any<CancellationToken>())
                  .Returns(new LibraryIngestionDataEvidence(false, false, false, false, true));
        harness.RepositoryFactory.GetLibraryRepository(ProfileName).Returns(namedLibraries);
        harness.RepositoryFactory.GetPageRepository(ProfileName).Returns(namedPages);
        harness.RepositoryFactory.GetChunkRepository(ProfileName).Returns(namedChunks);
        harness.RepositoryFactory.GetLibraryProfileRepository(ProfileName).Returns(namedProfiles);
        harness.RepositoryFactory.GetLibraryIndexRepository(ProfileName).Returns(namedIndexes);
        harness.RepositoryFactory.GetBm25ShardRepository(ProfileName).Returns(namedShards);
        harness.RepositoryFactory.GetSourceDocumentRepository(ProfileName).Returns(namedSources);
        harness.RepositoryFactory.GetLibraryIngestionModeRepository(ProfileName).Returns(namedModes);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Orchestrator.IngestAsync(
                                                                  Job(),
                                                                  ProfileName,
                                                                  ct: TestContext.Current.CancellationToken));

        await harness.ModeLeaseManager.Received(requiredNumberOfCalls: 1)
                     .TryAcquireAsync(ProfileName,
                                      "lib",
                                      LibraryIngestionMode.Web,
                                      Arg.Any<CancellationToken>());
        await namedLibraries.Received(requiredNumberOfCalls: 2)
                            .UpsertVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                                Arg.Any<CancellationToken>());
        await namedPages.Received(requiredNumberOfCalls: 1)
                        .GetPagesAsync("lib", "v2", Arg.Any<CancellationToken>());
        await namedShards.Received(requiredNumberOfCalls: 1)
                         .DeleteAsync("lib", "v2", Arg.Any<CancellationToken>());
        await namedIndexes.Received(requiredNumberOfCalls: 1)
                          .DeleteAsync("lib", "v2", Arg.Any<CancellationToken>());
        await namedSources.Received(requiredNumberOfCalls: 1)
                          .DeleteVersionAsync("lib", "v2", Arg.Any<CancellationToken>());
        await harness.LibraryRepository.DidNotReceiveWithAnyArgs()
                     .UpsertVersionAsync(default!, TestContext.Current.CancellationToken);
        await harness.PageRepository.DidNotReceiveWithAnyArgs()
                     .GetPagesAsync(default!, default!, TestContext.Current.CancellationToken);
        await harness.Shards.DidNotReceiveWithAnyArgs()
                     .DeleteAsync(default!, default!, TestContext.Current.CancellationToken);
        await harness.Indexes.DidNotReceiveWithAnyArgs()
                     .DeleteAsync(default!, default!, TestContext.Current.CancellationToken);
        await harness.SourceDocuments.DidNotReceiveWithAnyArgs()
                     .DeleteVersionAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NamedProfileSinglePageWritesNoDefaultRepository()
    {
        var harness = BuildHarness();
        var namedLibraries = Substitute.For<ILibraryRepository>();
        var namedPages = Substitute.For<IPageRepository>();
        var namedChunks = Substitute.For<IChunkRepository>();
        var namedProfiles = Substitute.For<ILibraryProfileRepository>();
        var namedIndexes = Substitute.For<ILibraryIndexRepository>();
        var namedShards = Substitute.For<IBm25ShardRepository>();
        var namedSources = Substitute.For<ISourceDocumentRepository>();
        var namedModes = Substitute.For<ILibraryIngestionModeRepository>();
        var storedChunks = new List<DocChunk>();
        namedChunks.UpsertChunksAsync(Arg.Do<IReadOnlyList<DocChunk>>(storedChunks.AddRange),
                                     Arg.Any<CancellationToken>())
                   .Returns(Task.CompletedTask);
        namedChunks.GetChunksAsync("lib", "v2", Arg.Any<CancellationToken>())
                   .Returns(_ => storedChunks.ToList());
        namedIndexes.GetAsync("lib", "v2", Arg.Any<CancellationToken>())
                    .Returns((LibraryIndex?)null);
        namedModes.GetLibraryDataEvidenceAsync("lib", Arg.Any<CancellationToken>())
                  .Returns(new LibraryIngestionDataEvidence(false, false, false, false, false));
        harness.RepositoryFactory.GetLibraryRepository(ProfileName).Returns(namedLibraries);
        harness.RepositoryFactory.GetPageRepository(ProfileName).Returns(namedPages);
        harness.RepositoryFactory.GetChunkRepository(ProfileName).Returns(namedChunks);
        harness.RepositoryFactory.GetLibraryProfileRepository(ProfileName).Returns(namedProfiles);
        harness.RepositoryFactory.GetLibraryIndexRepository(ProfileName).Returns(namedIndexes);
        harness.RepositoryFactory.GetBm25ShardRepository(ProfileName).Returns(namedShards);
        harness.RepositoryFactory.GetSourceDocumentRepository(ProfileName).Returns(namedSources);
        harness.RepositoryFactory.GetLibraryIngestionModeRepository(ProfileName).Returns(namedModes);
        harness.Crawler.SinglePage = new PageRecord
                                         {
                                             Id = "lib/v2/page",
                                             LibraryId = "lib",
                                             Version = "v2",
                                             Url = "https://example.test/page",
                                             Title = "Page",
                                             Category = DocCategory.Unclassified,
                                             RawContent = "# Heading\nA sufficiently descriptive profile-isolation paragraph.",
                                             FetchedAt = DateTime.UtcNow,
                                             ContentHash = "hash"
                                         };

        SinglePageIngestResult result = await harness.Orchestrator.IngestSinglePageAsync(
                                            "lib",
                                            "v2",
                                            "https://example.test/page",
                                            ProfileName,
                                            TestContext.Current.CancellationToken);

        Assert.Equal("Indexed", result.Status);
        await namedChunks.Received(requiredNumberOfCalls: 1)
                         .UpsertChunksAsync(Arg.Any<IReadOnlyList<DocChunk>>(),
                                            Arg.Any<CancellationToken>());
        await namedShards.Received(requiredNumberOfCalls: 1)
                         .ReplaceShardsAsync("lib",
                                             "v2",
                                             Arg.Any<IReadOnlyList<Bm25Shard>>(),
                                             Arg.Any<CancellationToken>());
        await namedIndexes.Received(requiredNumberOfCalls: 1)
                          .UpsertAsync(Arg.Any<LibraryIndex>(), Arg.Any<CancellationToken>());
        await harness.ChunkRepository.DidNotReceiveWithAnyArgs()
                     .UpsertChunksAsync(default!, TestContext.Current.CancellationToken);
        await harness.Shards.DidNotReceiveWithAnyArgs()
                     .ReplaceShardsAsync(default!,
                                         default!,
                                         default!,
                                         TestContext.Current.CancellationToken);
        await harness.Indexes.DidNotReceiveWithAnyArgs()
                     .UpsertAsync(default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(FailurePoint.Acquisition)]
    [InlineData(FailurePoint.Embedding)]
    [InlineData(FailurePoint.Bm25)]
    [InlineData(FailurePoint.VectorLoad)]
    public async Task FailureBeforePublicationLeavesPriorCurrentAndMarksCandidateFailed(FailurePoint point)
    {
        var harness = BuildHarness(point);

        await Assert.ThrowsAnyAsync<Exception>(() => harness.Orchestrator.IngestAsync(
            Job(), ct: TestContext.Current.CancellationToken));

        Assert.Equal("v1", harness.ExistingLibrary.CurrentVersion);
        Assert.DoesNotContain(VersionPublicationState.Published, harness.States);
        Assert.Equal(VersionPublicationState.Failed, harness.States[^1]);
        Assert.Equal(1, harness.Vector.RemoveCalls);
        await harness.Shards.Received(1)
                     .DeleteAsync("lib", "v2", Arg.Any<CancellationToken>());
        await harness.Indexes.Received(1)
                      .DeleteAsync("lib", "v2", Arg.Any<CancellationToken>());
        await harness.SourceDocuments.Received(1)
                     .DeleteVersionAsync("lib", "v2", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancellationLeavesPriorCurrentMarksCandidateFailedAndRemovesCandidateIndex()
    {
        var harness = BuildHarness(FailurePoint.Cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Orchestrator.IngestAsync(
            Job(), ct: TestContext.Current.CancellationToken));

        Assert.Equal("v1", harness.ExistingLibrary.CurrentVersion);
        Assert.Equal(VersionPublicationState.Failed, harness.States[^1]);
        Assert.Equal(1, harness.Vector.RemoveCalls);
        await harness.Shards.Received(1)
                     .DeleteAsync("lib", "v2", Arg.Any<CancellationToken>());
        await harness.Indexes.Received(1)
                      .DeleteAsync("lib", "v2", Arg.Any<CancellationToken>());
        await harness.SourceDocuments.Received(1)
                     .DeleteVersionAsync("lib", "v2", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LostWebModeLeasePreventsStaleCandidateCleanup()
    {
        var harness = BuildHarness(FailurePoint.Acquisition);
        harness.ModeLease.TryRenewAsync(Arg.Any<CancellationToken>()).Returns(false);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Orchestrator.IngestAsync(Job(), ct: TestContext.Current.CancellationToken));

        Assert.Equal("acquisition-failed", exception.Message);
        Assert.Equal(new[] { VersionPublicationState.Building }, harness.States);
        Assert.Equal(0, harness.Vector.RemoveCalls);
        await harness.Shards.DidNotReceive()
                     .DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Indexes.DidNotReceive()
                      .DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.SourceDocuments.DidNotReceive()
                     .DeleteVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        string detail = Assert.IsType<string>(exception.Data["SaddleRAG.CandidateCleanupFailure"]);
        Assert.Contains("no longer owns its source-mode lease", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanupFailurePreservesOriginalIngestionFailureAndExposesCleanupDetail()
    {
        var harness = BuildHarness(FailurePoint.Acquisition, cleanupFails: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Orchestrator.IngestAsync(
            Job(), ct: TestContext.Current.CancellationToken));

        Assert.Equal("acquisition-failed", exception.Message);
        var cleanupDetail = Assert.IsType<string>(exception.Data["SaddleRAG.CandidateCleanupFailure"]);
        Assert.Contains("delete candidate BM25 shards", cleanupDetail, StringComparison.Ordinal);
        await harness.Indexes.Received(1)
                      .DeleteAsync("lib", "v2", Arg.Any<CancellationToken>());
        Assert.Equal(1, harness.Vector.RemoveCalls);
    }

    private static Harness BuildHarness(FailurePoint point = FailurePoint.None,
                                         bool publishedVersionExists = false,
                                         bool cleanupFails = false,
                                         bool directoryDefinitionExists = false)
    {
        var events = new List<string>();
        var states = new List<VersionPublicationState>();
        var crawler = new StubCrawler
                          {
                              Error = point == FailurePoint.Acquisition
                                          ? new InvalidOperationException("acquisition-failed")
                                          : null,
                              Cancel = point == FailurePoint.Cancellation,
                              OnStart = () => events.Add("acquisition")
                          };
        var classifier = Substitute.For<ILlmClassifier>();
        classifier.BackendName.Returns("test");
        classifier.ModelId.Returns("test-model");
        classifier.GetCurrentVersion().Returns("test-model-v1");
        classifier.ClassifyAsync(Arg.Any<PageRecord>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns((DocCategory.HowTo, 0.9f));

        var embedding = Substitute.For<IEmbeddingProvider>();
        embedding.ProviderId.Returns("test");
        embedding.ModelName.Returns("embed-model");
        embedding.Dimensions.Returns(2);
        if (point == FailurePoint.Embedding)
        {
            embedding.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<EmbedRole>(),
                                 Arg.Any<CancellationToken>())
                     .Returns<float[][]>(_ => throw new InvalidOperationException("embedding-failed"));
        }
        else
        {
            embedding.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<EmbedRole>(),
                                 Arg.Any<CancellationToken>())
                     .Returns(call => call.Arg<IReadOnlyList<string>>()!.Select(_ => new[] { 1f, 0f }).ToArray());
        }

        var pageRepo = Substitute.For<IPageRepository>();
        pageRepo.GetPagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<PageRecord>());
        var storedChunks = new List<DocChunk>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        chunkRepo.UpsertChunksAsync(Arg.Do<IReadOnlyList<DocChunk>>(c => storedChunks.AddRange(c)),
                                    Arg.Any<CancellationToken>())
                 .Returns(Task.CompletedTask);
        chunkRepo.GetChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(_ => storedChunks.ToList());
        chunkRepo.GetLanguageMixAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<string, double>());
        chunkRepo.GetHostnameDistributionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<string, int> { ["a"] = 1, ["b"] = 1, ["c"] = 1 });
        chunkRepo.GetSampleTitlesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                                       Arg.Any<CancellationToken>())
                 .Returns(new[] { "one", "two", "three" });

        var existing = new LibraryRecord
                           {
                               Id = "lib", Name = "lib", Hint = "h",
                               CurrentVersion = publishedVersionExists ? "v2" : "v1",
                               AllVersions = publishedVersionExists ? ["v2"] : ["v1"]
                           };
        var libraryRepo = Substitute.For<ILibraryRepository>();
        libraryRepo.GetLibraryAsync("lib", Arg.Any<CancellationToken>()).Returns(existing);
        LibraryVersionRecord? publishedVersion = publishedVersionExists
                                                     ? new LibraryVersionRecord
                                                           {
                                                               Id = "lib/v2",
                                                               LibraryId = "lib",
                                                               Version = "v2",
                                                               ScrapedAt = DateTime.UtcNow,
                                                               PageCount = 1,
                                                               ChunkCount = 1,
                                                               EmbeddingProviderId = "test",
                                                               EmbeddingModelName = "embed-model",
                                                               EmbeddingDimensions = 2,
                                                               PublicationState = VersionPublicationState.Published
                                                           }
                                                     : null;
        libraryRepo.GetVersionAsync("lib", "v2", Arg.Any<CancellationToken>())
                   .Returns(_ => publishedVersion);
        libraryRepo.UpsertVersionAsync(Arg.Do<LibraryVersionRecord>(record =>
                                              {
                                                  states.Add(record.PublicationState);
                                                  events.Add(record.PublicationState switch
                                                      {
                                                          VersionPublicationState.Building => "building",
                                                          VersionPublicationState.Published => "published",
                                                          VersionPublicationState.Failed => "failed",
                                                          var _ => "unknown"
                                                      });
                                              }),
                                           Arg.Any<CancellationToken>())
                   .Returns(Task.CompletedTask);
        libraryRepo.UpsertLibraryAsync(Arg.Do<LibraryRecord>(_ => events.Add("pointer")),
                                       Arg.Any<CancellationToken>())
                   .Returns(Task.CompletedTask);

        var shards = Substitute.For<IBm25ShardRepository>();
        if (point == FailurePoint.Bm25)
        {
            shards.ReplaceShardsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<Bm25Shard>>(),
                                      Arg.Any<CancellationToken>())
                  .Returns(Task.FromException(new InvalidOperationException("bm25-failed")));
        }
        if (cleanupFails)
        {
            shards.DeleteAsync("lib", "v2", Arg.Any<CancellationToken>())
                  .Returns(Task.FromException<long>(new InvalidOperationException("bm25-cleanup-failed")));
        }
        var indexes = Substitute.For<ILibraryIndexRepository>();
        indexes.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns((LibraryIndex?) null);
        indexes.UpsertAsync(Arg.Any<LibraryIndex>(), Arg.Any<CancellationToken>())
               .Returns(_ => { events.Add("bm25"); return Task.CompletedTask; });
        var profiles = Substitute.For<ILibraryProfileRepository>();
        var sources = Substitute.For<ISourceDocumentRepository>();
        var modes = Substitute.For<ILibraryIngestionModeRepository>();
        var modeLeaseManager = Substitute.For<ILibraryIngestionModeLeaseManager>();
        var modeLease = Substitute.For<ILibraryIngestionModeLease>();
        sources.GetDirectoryDefinitionAsync("lib", Arg.Any<CancellationToken>())
               .Returns(directoryDefinitionExists
                            ? DirectoryDefinition()
                            : (DirectoryLibraryDefinition?) null);
        sources.PublishCandidateScanRunAsync("lib",
                                             "v2",
                                             Arg.Any<string>(),
                                             Arg.Any<CancellationToken>())
               .Returns(_ =>
                        {
                            events.Add("documents-published");
                            return 1L;
                        });
        sources.DeleteVersionAsync("lib", "v2", Arg.Any<CancellationToken>())
               .Returns(_ =>
                        {
                            events.Add("documents-cleaned");
                            return 1L;
                        });
        var repositoryFactory = Substitute.For<RepositoryFactory>([null!]);
        repositoryFactory.GetSourceDocumentRepository(Arg.Any<string?>()).Returns(sources);
        repositoryFactory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);
        repositoryFactory.GetLibraryIngestionModeRepository(Arg.Any<string?>()).Returns(modes);
        modes.GetLibraryDataEvidenceAsync("lib", Arg.Any<CancellationToken>())
             .Returns(new LibraryIngestionDataEvidence(true, false, true, true, false));
        modeLease.OwnershipStateAtAcquisition.Returns(LibraryIngestionOwnershipState.Reserved);
        modeLease.OwnershipLostToken.Returns(CancellationToken.None);
        modeLease.TryRenewAsync(Arg.Any<CancellationToken>()).Returns(true);
        modeLease.TryCommitAsync(Arg.Any<CancellationToken>()).Returns(true);
        modeLease.TryReconcileReservedModeAsync(Arg.Any<LibraryIngestionMode>(),
                                                 Arg.Any<CancellationToken>())
                 .Returns(true);
        modeLeaseManager.TryAcquireAsync(Arg.Any<string?>(),
                                         "lib",
                                         LibraryIngestionMode.Web,
                                         Arg.Any<CancellationToken>())
                        .Returns(modeLease);
        var vector = new RecordingVector(events, failFullLoad: point == FailurePoint.VectorLoad);
        var broadcaster = Substitute.For<IMonitorBroadcaster>();
        var orchestrator = new IngestionOrchestrator(crawler,
                                                     classifier,
                                                     new CategoryAwareChunker(new SymbolExtractor()),
                                                     embedding,
                                                     vector,
                                                     libraryRepo,
                                                     pageRepo,
                                                     chunkRepo,
                                                     profiles,
                                                     indexes,
                                                     shards,
                                                     new SuspectDetector(),
                                                     Substitute.For<IScrapeAuditWriter>(),
                                                     broadcaster,
                                                     NullLogger<IngestionOrchestrator>.Instance,
                                                     sources,
                                                     repositoryFactory,
                                                     modeLeaseManager);
        return new Harness(orchestrator,
                           crawler,
                           existing,
                           events,
                           states,
                           vector,
                           libraryRepo,
                           pageRepo,
                           chunkRepo,
                           shards,
                           indexes,
                           sources,
                           repositoryFactory,
                           modeLeaseManager,
                           modeLease);
    }

    private static ScrapeJob Job() => new()
        {
            LibraryId = "lib", Version = "v2", RootUrl = "https://example.test/", LibraryHint = "h",
            AllowedUrlPatterns = ["example.test"]
        };

    private static DirectoryLibraryDefinition DirectoryDefinition() => new()
        {
            Id = "lib",
            RootPath = "C:\\manuals",
            BindingStatus = DirectoryLibraryBindingStatus.Bound,
            RegisteredAtUtc = DateTime.UtcNow
        };

    private static void AssertOrdered(List<string> events, params string[] expected)
    {
        var previous = -1;
        foreach(var value in expected)
        {
            var current = events.IndexOf(value);
            Assert.True(current > previous, $"Expected '{value}' after index {previous}; events: {string.Join(",", events)}");
            previous = current;
        }
    }

    public enum FailurePoint
    {
        None,
        Acquisition,
        Embedding,
        Bm25,
        VectorLoad,
        Cancellation
    }

    private sealed record Harness(IngestionOrchestrator Orchestrator,
                                  StubCrawler Crawler,
                                  LibraryRecord ExistingLibrary,
                                  List<string> Events,
                                  List<VersionPublicationState> States,
                                  RecordingVector Vector,
                                  ILibraryRepository LibraryRepository,
                                  IPageRepository PageRepository,
                                  IChunkRepository ChunkRepository,
                                  IBm25ShardRepository Shards,
                                  ILibraryIndexRepository Indexes,
                                  ISourceDocumentRepository SourceDocuments,
                                  RepositoryFactory RepositoryFactory,
                                  ILibraryIngestionModeLeaseManager ModeLeaseManager,
                                  ILibraryIngestionModeLease ModeLease);

    private const string ProfileName = "team-profile";

    private sealed class StubCrawler : IPageCrawler
    {
        public Exception? Error { get; init; }
        public bool Cancel { get; init; }
        public Action? OnStart { get; init; }
        public PageRecord? SinglePage { get; set; }

        public async Task CrawlAsync(ScrapeJob job,
                                     ChannelWriter<PageRecord> output,
                                     string jobId = "",
                                     IReadOnlySet<string>? resumeUrls = null,
                                     IReadOnlyList<string>? seedUrls = null,
                                     Action<int>? onPageFetched = null,
                                     Action<int>? onQueued = null,
                                     Action? onFetchError = null,
                                     IngestionPersistenceMode persistMode = IngestionPersistenceMode.Full,
                                     DryRunAccumulator? dryRunAcc = null,
                                     CancellationToken ct = default)
        {
            OnStart?.Invoke();
            if (Cancel)
                throw new OperationCanceledException(ct);
            if (Error != null)
                throw Error;
            var page = new PageRecord
                           {
                               Id = "lib/v2/page", LibraryId = "lib", Version = "v2",
                               Url = "https://example.test/page", Title = "Page", Category = DocCategory.Unclassified,
                               RawContent = "# Heading\nA sufficiently descriptive paragraph for lifecycle publication testing.",
                               FetchedAt = DateTime.UtcNow, ContentHash = "hash"
                           };
            await output.WriteAsync(page, ct);
            onPageFetched?.Invoke(1);
            output.TryComplete();
        }

        public Task<PageRecord?> FetchSinglePageAsync(string libraryId,
                                                       string version,
                                                       string url,
                                                       CancellationToken ct = default)
        {
            OnStart?.Invoke();
            return Task.FromResult(SinglePage);
        }
    }

    private sealed class RecordingVector(List<string> events, bool failFullLoad) : IVectorSearchProvider
    {
        public int RemoveCalls { get; private set; }
        public int IndexCalls => mIndexCalls;
        private int mIndexCalls;

        public Task IndexChunksAsync(string? profile,
                                     string libraryId,
                                     string version,
                                     IReadOnlyList<DocChunk> chunks,
                                     CancellationToken ct = default)
        {
            mIndexCalls++;
            events.Add(mIndexCalls == 1 ? "vector-batch" : "vector-full");
            if (failFullLoad && mIndexCalls == 2)
                throw new InvalidOperationException("vector-load-failed");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(float[] queryEmbedding,
                                                                   VectorSearchFilter filter,
                                                                   int maxResults = 5,
                                                                   CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);

        public Task RemoveIndexAsync(string? profile,
                                     string libraryId,
                                     string version,
                                     CancellationToken ct = default)
        {
            RemoveCalls++;
            events.Add("vector-remove");
            return Task.CompletedTask;
        }

        public Task RemoveLibraryIndexesAsync(string? profile,
                                              string libraryId,
                                              CancellationToken ct = default) => Task.CompletedTask;
    }
}
