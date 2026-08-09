// LibraryImporterEncoderCompatibilityTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Packaging;
using SaddleRAG.Tests.Packaging.Fixtures;

#endregion

namespace SaddleRAG.Tests.Packaging;

public sealed class LibraryImporterEncoderCompatibilityTests
{
    private const string LibraryId = "mixed-encoder-library";
    private const string FirstVersion = "1.0";
    private const string SecondVersion = "2.0";
    private const string ProviderId = "onnx-local";
    private const string ActiveModelName = "active-model";
    private const string MismatchingModelName = "other-model";
    private const string PublishSummaryOperation = "publish-summary";
    private const string PersistJobOperation = "persist-reembed-job";
    private const string DispatchJobOperation = "dispatch-reembed-job";
    private const int Dimensions = PackagingFixtures.DefaultDim;

    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(2, false)]
    public async Task MixedEncoderPackageUsesEachVersionCompatibility(int manifestVersion,
                                                                      bool mismatchFirst)
    {
        string mismatchingVersion = mismatchFirst ? FirstVersion : SecondVersion;
        string matchingVersion = mismatchFirst ? SecondVersion : FirstVersion;
        IReadOnlyDictionary<string, float[]> expectedEmbeddings = CreateExpectedEmbeddings();
        string bundlePath = await CreateMixedEncoderBundleAsync(manifestVersion,
                                                                 mismatchingVersion,
                                                                 expectedEmbeddings);

        var libraryRepository = Substitute.For<ILibraryRepository>();
        libraryRepository.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
                         .Returns((LibraryRecord?) null);
        libraryRepository.TryClaimImportVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                                      Arg.Any<string>(),
                                                      Arg.Any<CancellationToken>())
                         .Returns(true);
        libraryRepository.TryPublishImportVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                                        Arg.Any<string>(),
                                                        Arg.Any<CancellationToken>())
                         .Returns(true);

        var jobs = new List<JobRecord>();
        var operations = new List<string>();
        var jobRepository = Substitute.For<IJobRepository>();
        jobRepository.ListActiveAsync(Arg.Any<string>(),
                                      Arg.Any<string?>(),
                                      Arg.Any<JobType?>(),
                                      Arg.Any<CancellationToken>())
                     .Returns(Array.Empty<JobRecord>() as IReadOnlyList<JobRecord>);
        jobRepository.When(repository => repository.UpsertAsync(Arg.Any<JobRecord>(),
                                                                 Arg.Any<CancellationToken>()))
                     .Do(call =>
                          {
                              jobs.Add(call.ArgAt<JobRecord>(position: 0));
                              operations.Add(PersistJobOperation);
                          });

        libraryRepository.When(repository => repository.UpsertLibraryAsync(
                                   Arg.Any<LibraryRecord>(),
                                   Arg.Any<CancellationToken>()))
                         .Do(_ => operations.Add(PublishSummaryOperation));

        var dispatchedJobs = new List<JobRecord>();
        var reembedJobDispatcher = Substitute.For<IReembedJobDispatcher>();
        reembedJobDispatcher.TryDispatchPersisted(Arg.Any<JobRecord>())
                            .Returns(call =>
                                     {
                                         dispatchedJobs.Add(call.ArgAt<JobRecord>(position: 0));
                                         operations.Add(DispatchJobOperation);
                                         return true;
                                     });

        var embeddingProvider = Substitute.For<IEmbeddingProvider>();
        embeddingProvider.ProviderId.Returns(ProviderId);
        embeddingProvider.ModelName.Returns(ActiveModelName);
        embeddingProvider.Dimensions.Returns(Dimensions);

        var insertedChunks = new List<DocChunk>();
        var chunkRepository = Substitute.For<IChunkRepository>();
        chunkRepository.When(repository => repository.InsertChunksAsync(
                                 Arg.Any<IReadOnlyList<DocChunk>>(),
                                 Arg.Any<CancellationToken>()))
                       .Do(call => insertedChunks.AddRange(call.ArgAt<IReadOnlyList<DocChunk>>(0)));

        var profileRepository = Substitute.For<ILibraryProfileRepository>();
        var indexRepository = Substitute.For<ILibraryIndexRepository>();
        var excludedRepository = Substitute.For<IExcludedSymbolsRepository>();
        var diffRepository = Substitute.For<IDiffRepository>();
        var pageRepository = Substitute.For<IPageRepository>();
        var bm25Repository = Substitute.For<IBm25ShardRepository>();
        PackagingImportLifecycle lifecycle = PackagingImportLifecycle.Create(libraryRepository,
            profileRepository, indexRepository, excludedRepository, diffRepository, pageRepository,
            chunkRepository, bm25Repository);
        var importer = new LibraryImporter(libraryRepository,
                                           jobRepository,
                                           embeddingProvider,
                                           profileRepository,
                                           indexRepository,
                                           excludedRepository,
                                           diffRepository,
                                           pageRepository,
                                           chunkRepository,
                                           bm25Repository,
                                           deletionService: lifecycle.DeletionService,
                                           modeLeaseManager: lifecycle.ModeLeaseManager,
                                           modeRepository: lifecycle.ModeRepository,
                                           reembedJobDispatcher: reembedJobDispatcher);

        ImportResult result = await importer.ImportAsync(
                                  new ImportRequest { BundlePath = bundlePath },
                                  progress: null,
                                  ct: TestContext.Current.CancellationToken);

        Assert.Equal(new[] { FirstVersion, SecondVersion }, result.VersionsImported);
        Assert.Empty(result.PartialFailures);

        DocChunk mismatchingChunk = Assert.Single(insertedChunks,
                                                   chunk => chunk.Version == mismatchingVersion);
        Assert.Null(mismatchingChunk.Embedding);
        DocChunk matchingChunk = Assert.Single(insertedChunks,
                                                chunk => chunk.Version == matchingVersion);
        Assert.Equal(expectedEmbeddings[matchingVersion], matchingChunk.Embedding);

        JobRecord reembedJob = Assert.Single(jobs);
        Assert.Equal(JobType.Reembed, reembedJob.JobType);
        Assert.Equal(LibraryId, reembedJob.LibraryId);
        Assert.Equal(mismatchingVersion, reembedJob.Version);
        Assert.Equal(JobStatus.Queued, reembedJob.Status);
        Assert.Equal(new[] { reembedJob.Id }, result.PendingReembedJobIds);
        Assert.Same(reembedJob, Assert.Single(dispatchedJobs));
        Assert.Equal([PublishSummaryOperation, PersistJobOperation, DispatchJobOperation], operations);
    }

    private static IReadOnlyDictionary<string, float[]> CreateExpectedEmbeddings() =>
        new Dictionary<string, float[]>(StringComparer.Ordinal)
            {
                [FirstVersion] = Enumerable.Range(0, Dimensions)
                                           .Select(index => index + 0.25f)
                                           .ToArray(),
                [SecondVersion] = Enumerable.Range(0, Dimensions)
                                            .Select(index => index + 10.25f)
                                            .ToArray()
            };

    private static async Task<string> CreateMixedEncoderBundleAsync(
        int manifestVersion,
        string mismatchingVersion,
        IReadOnlyDictionary<string, float[]> expectedEmbeddings)
    {
        string[] versions = [FirstVersion, SecondVersion];
        LibraryRecord library = PackagingFixtures.MakeLibrary(LibraryId,
                                                               SecondVersion,
                                                               versions);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                          {
                              [BundlePaths.LibraryFile] = JsonSerializer.SerializeToUtf8Bytes(
                                  library,
                                  BundleJsonOptions.Default)
                          };
        var versionEntries = new List<BundleVersionEntry>();
        foreach(string version in versions)
        {
            string modelName = version == mismatchingVersion
                                   ? MismatchingModelName
                                   : ActiveModelName;
            LibraryVersionRecord versionRecord = PackagingFixtures.MakeVersion(LibraryId,
                version,
                pageCount: 0,
                chunkCount: 1,
                dim: Dimensions,
                modelName: modelName);
            DocChunk chunk = PackagingFixtures.MakeChunks(LibraryId,
                                                           version,
                                                           count: 1,
                                                           dim: Dimensions)[0] with
                                 {
                                     Embedding = expectedEmbeddings[version]
                                 };
            string versionPath = BundlePaths.VersionFilePath(version, BundlePaths.VersionFile);
            string chunksPath = BundlePaths.VersionFilePath(version, BundlePaths.ChunksFile);
            string embeddingsPath = BundlePaths.VersionFilePath(version,
                                                                 BundlePaths.EmbeddingsBlobFile);
            entries[versionPath] = JsonSerializer.SerializeToUtf8Bytes(versionRecord,
                                                                        BundleJsonOptions.Default);
            entries[chunksPath] = SerializeJsonl(chunk with { Embedding = null });
            entries[embeddingsPath] = SerializeEmbedding(expectedEmbeddings[version]);
            var blobs = new Dictionary<string, BlobInfo>(StringComparer.Ordinal)
                            {
                                [versionPath] = Describe(entries[versionPath]),
                                [chunksPath] = Describe(entries[chunksPath]),
                                [embeddingsPath] = Describe(entries[embeddingsPath])
                            };
            versionEntries.Add(new BundleVersionEntry
                                   {
                                       Version = version,
                                       EmbeddingProviderId = ProviderId,
                                       EmbeddingModelName = modelName,
                                       EmbeddingDimensions = Dimensions,
                                       PageCount = 0,
                                       ChunkCount = 1,
                                       Bm25HasGridFs = false,
                                       Blobs = blobs
                                   });
        }

        var manifest = new BundleManifest
                           {
                               ManifestVersion = manifestVersion,
                               ExporterVersion = "test",
                               CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                               Library = new BundleLibraryInfo
                                             {
                                                 Id = LibraryId,
                                                 Name = LibraryId,
                                                 Hint = "mixed encoder fixture"
                                             },
                               Blobs = new Dictionary<string, BlobInfo>(StringComparer.Ordinal)
                                           {
                                               [BundlePaths.LibraryFile] = Describe(
                                                   entries[BundlePaths.LibraryFile])
                                           },
                               Versions = versionEntries
                           };

        string path = Path.Combine(Path.GetTempPath(),
                                   $"saddlerag-mixed-encoder-{Guid.NewGuid():N}.srlib.zip");
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        foreach((string entryPath, byte[] content) in entries)
        {
            await using Stream stream = archive.CreateEntry(entryPath).Open();
            await stream.WriteAsync(content, TestContext.Current.CancellationToken);
        }
        await using Stream manifestStream = archive.CreateEntry(BundlePaths.ManifestFile).Open();
        await JsonSerializer.SerializeAsync(manifestStream,
                                            manifest,
                                            BundleJsonOptions.Default,
                                            TestContext.Current.CancellationToken);
        return path;
    }

    private static byte[] SerializeJsonl<T>(T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, BundleJsonOptions.JsonlDefault);
        var result = new byte[json.Length + 1];
        json.CopyTo(result, 0);
        result[^1] = (byte) '\n';
        return result;
    }

    private static byte[] SerializeEmbedding(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static BlobInfo Describe(byte[] content) =>
        new()
            {
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
                Bytes = content.LongLength
            };
}
