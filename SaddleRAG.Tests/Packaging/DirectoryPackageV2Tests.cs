// DirectoryPackageV2Tests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Packaging;
using SaddleRAG.Tests.Packaging.Fixtures;

namespace SaddleRAG.Tests.Packaging;

public sealed class DirectoryPackageV2Tests : IAsyncLifetime
{
    private readonly List<string> mTemporaryPaths = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        foreach(string path in mTemporaryPaths.Where(File.Exists))
            File.Delete(path);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ExportV2ContainsExactArtifactsHashesProvenanceSubjectsAndNoMachineRoot()
    {
        ExportFixture fixture = BuildDirectoryExportFixture();
        string outputPath = TemporaryPath("stage7-v2");

        ExportResult result = await fixture.Exporter.ExportAsync(new ExportRequest
                                                                      {
                                                                          LibraryId =
                                                                              DirectoryPackagingFixtures.LibraryId,
                                                                          Versions = VersionFilter.Current,
                                                                          OutputPath = outputPath
                                                                      },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.Equal([DirectoryPackagingFixtures.Version], result.VersionsExported);
        using ZipArchive archive = ZipFile.OpenRead(outputPath);
        JsonObject manifest = await ReadJsonObjectAsync(archive, BundlePaths.ManifestFile);
        Assert.Equal(2, manifest["manifestVersion"]!.GetValue<int>());
        JsonObject directory = manifest["directory"]!.AsObject();
        Assert.True(directory["recursive"]!.GetValue<bool>());
        Assert.Equal([".pdf", ".docx", ".txt"],
                     directory["allowedExtensions"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(["**/bin/**", "**/.git/**"],
                     directory["exclusionPatterns"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Null(directory["rootPath"]);

        string originalPath = ArtifactPath(DirectoryPackagingFixtures.Hash(
                                               DirectoryPackagingFixtures.OriginalBytes));
        string extractionPath = ArtifactPath(DirectoryPackagingFixtures.Hash(
                                                 DirectoryPackagingFixtures.ExtractionBytes));
        Assert.Equal(DirectoryPackagingFixtures.OriginalBytes,
                     await ReadEntryBytesAsync(archive, originalPath));
        Assert.Equal(DirectoryPackagingFixtures.ExtractionBytes,
                     await ReadEntryBytesAsync(archive, extractionPath));
        JsonObject blobs = manifest["blobs"]!.AsObject();
        Assert.Equal(DirectoryPackagingFixtures.Hash(DirectoryPackagingFixtures.OriginalBytes),
                     blobs[originalPath]!["sha256"]!.GetValue<string>());
        Assert.Equal(DirectoryPackagingFixtures.OriginalBytes.LongLength,
                     blobs[originalPath]!["bytes"]!.GetValue<long>());
        Assert.Equal(DirectoryPackagingFixtures.Hash(DirectoryPackagingFixtures.ExtractionBytes),
                     blobs[extractionPath]!["sha256"]!.GetValue<string>());

        SourceDocumentRecord source = await ReadSingleJsonlAsync<SourceDocumentRecord>(archive,
            SourcesPath);
        DocumentRevisionRecord revision = await ReadSingleJsonlAsync<DocumentRevisionRecord>(
            archive,
            VersionPath(DocumentRevisionsFile));
        SubjectCatalogRecord catalog = await ReadSingleJsonlAsync<SubjectCatalogRecord>(archive,
            SubjectCatalogsPath);
        SubjectAssignmentRecord assignment = await ReadSingleJsonlAsync<SubjectAssignmentRecord>(
            archive,
            VersionPath(SubjectAssignmentsFile));
        PageRecord page = await ReadSingleJsonlAsync<PageRecord>(archive,
            BundlePaths.VersionFilePath(DirectoryPackagingFixtures.Version, BundlePaths.PagesFile));
        DocChunk chunk = await ReadSingleJsonlAsync<DocChunk>(archive,
            BundlePaths.VersionFilePath(DirectoryPackagingFixtures.Version, BundlePaths.ChunksFile));

        Assert.Equal(DirectoryPackagingFixtures.Source(), source);
        Assert.Equivalent(DirectoryPackagingFixtures.Revision(), revision, strict: true);
        Assert.Equivalent(DirectoryPackagingFixtures.ExtractionProvenance(),
                          revision.ExtractionProvenance,
                          strict: true);
        Assert.Equivalent(DirectoryPackagingFixtures.Catalog(), catalog, strict: true);
        Assert.Equivalent(DirectoryPackagingFixtures.Assignment(), assignment, strict: true);
        Assert.Equal(DirectoryPackagingFixtures.Provenance(), page.DocumentSource);
        Assert.Equal(DirectoryPackagingFixtures.Provenance(), chunk.DocumentSource);
        Assert.Equal([DirectoryPackagingFixtures.SubjectId,
                      DirectoryPackagingFixtures.SecondarySubjectId],
                     page.SubjectIds);
        Assert.Equal(page.SubjectIds, chunk.SubjectIds);
        await AssertArchiveDoesNotContainRootAsync(archive, DirectoryPackagingFixtures.RootPath);
    }

    [Fact]
    public async Task ManifestV1PackageWithoutDocumentSectionsRemainsImportable()
    {
        string v2Path = TemporaryPath("stage7-web-v2");
        string v1Path = TemporaryPath("stage7-web-v1");
        ExportFixture export = BuildLegacyWebExportFixture();
        await export.Exporter.ExportAsync(new ExportRequest
                                              {
                                                  LibraryId = LegacyLibraryId,
                                                  Versions = VersionFilter.Current,
                                                  OutputPath = v2Path
                                              },
                                          progress: null,
                                          TestContext.Current.CancellationToken);
        RewriteAsLegacyV1(v2Path, v1Path);
        ImportFixture import = BuildImportFixture();

        ImportResult result = await import.Importer.ImportAsync(new ImportRequest { BundlePath = v1Path },
                                                                 progress: null,
                                                                 TestContext.Current.CancellationToken);

        Assert.Contains(LegacyVersion, result.VersionsImported);
        Assert.Empty(result.PartialFailures);
        await import.Pages.Received()
                    .UpsertPageAsync(Arg.Is<PageRecord>(page => IsLegacyPage(page)),
                                     Arg.Any<CancellationToken>());
        await import.Chunks.Received()
                    .InsertChunksAsync(Arg.Is<IReadOnlyList<DocChunk>>(chunks => IsLegacyChunks(chunks)),
                                       Arg.Any<CancellationToken>());
        await import.Sources.DidNotReceiveWithAnyArgs()
                    .PersistRevisionAsync(default!, default!, default, TestContext.Current.CancellationToken);
        await import.Assignments.DidNotReceiveWithAnyArgs()
                    .PersistAsync(default!, TestContext.Current.CancellationToken);
        await import.Catalogs.DidNotReceiveWithAnyArgs()
                    .InsertRevisionAsync(default!, TestContext.Current.CancellationToken);
    }

    private string TemporaryPath(string stem)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{stem}-{Guid.NewGuid():N}.srlib.zip");
        mTemporaryPaths.Add(path);
        return path;
    }

    private static bool IsLegacyPage(PageRecord? page)
    {
        bool result = page is not null
                      && page.LibraryId == LegacyLibraryId
                      && page.Version == LegacyVersion;
        return result;
    }

    private static bool IsLegacyChunks(IReadOnlyList<DocChunk>? chunks)
    {
        bool result = chunks is not null
                      && chunks.Count == 1
                      && chunks[index: 0].LibraryId == LegacyLibraryId
                      && chunks[index: 0].Version == LegacyVersion;
        return result;
    }

    private static ExportFixture BuildDirectoryExportFixture()
    {
        var libraries = Substitute.For<ILibraryRepository>();
        var profiles = Substitute.For<ILibraryProfileRepository>();
        var indexes = Substitute.For<ILibraryIndexRepository>();
        var excluded = Substitute.For<IExcludedSymbolsRepository>();
        var diffs = Substitute.For<IDiffRepository>();
        var pages = Substitute.For<IPageRepository>();
        var chunks = Substitute.For<IChunkRepository>();
        var bm25 = EmptyBm25();
        var sources = Substitute.For<ISourceDocumentRepository>();
        var catalogs = Substitute.For<ISubjectCatalogRepository>();
        var assignments = Substitute.For<ISubjectAssignmentRepository>();
        libraries.GetLibraryAsync(DirectoryPackagingFixtures.LibraryId, Arg.Any<CancellationToken>())
                 .Returns(DirectoryPackagingFixtures.Library());
        libraries.GetVersionAsync(DirectoryPackagingFixtures.LibraryId,
                                  DirectoryPackagingFixtures.Version,
                                  Arg.Any<CancellationToken>())
                 .Returns(DirectoryPackagingFixtures.LibraryVersion());
        profiles.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((LibraryProfile?) null);
        indexes.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns((LibraryIndex?) null);
        excluded.ListAsync(Arg.Any<string>(),
                           Arg.Any<string>(),
                           Arg.Any<SymbolRejectionReason?>(),
                           Arg.Any<int>(),
                           Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ExcludedSymbol>());
        pages.GetPagesAsync(DirectoryPackagingFixtures.LibraryId,
                            DirectoryPackagingFixtures.Version,
                            Arg.Any<CancellationToken>())
             .Returns([DirectoryPackagingFixtures.Page()]);
        chunks.GetChunksAsync(DirectoryPackagingFixtures.LibraryId,
                              DirectoryPackagingFixtures.Version,
                              Arg.Any<CancellationToken>())
              .Returns([DirectoryPackagingFixtures.Chunk()]);
        sources.GetDirectoryDefinitionAsync(DirectoryPackagingFixtures.LibraryId,
                                             Arg.Any<CancellationToken>())
               .Returns(DirectoryPackagingFixtures.DirectoryDefinition());
        sources.GetRevisionsAsync(DirectoryPackagingFixtures.LibraryId,
                                   DirectoryPackagingFixtures.Version,
                                   Arg.Any<CancellationToken>())
               .Returns([DirectoryPackagingFixtures.Revision()]);
        sources.GetDocumentAsync(DirectoryPackagingFixtures.DocumentId, Arg.Any<CancellationToken>())
               .Returns(DirectoryPackagingFixtures.Source());
        sources.OpenArtifactAsync(DirectoryPackagingFixtures.Hash(DirectoryPackagingFixtures.OriginalBytes),
                                  Arg.Any<CancellationToken>())
               .Returns(_ => new MemoryStream(DirectoryPackagingFixtures.OriginalBytes, writable: false));
        sources.OpenArtifactAsync(DirectoryPackagingFixtures.Hash(DirectoryPackagingFixtures.ExtractionBytes),
                                  Arg.Any<CancellationToken>())
               .Returns(_ => new MemoryStream(DirectoryPackagingFixtures.ExtractionBytes, writable: false));
        assignments.GetByDocumentRevisionIdsAsync(Arg.Any<IReadOnlyCollection<string>>(),
                                                   Arg.Any<CancellationToken>())
                   .Returns([DirectoryPackagingFixtures.Assignment()]);
        catalogs.GetManyAsync(Arg.Any<IReadOnlyCollection<SubjectCatalogKey>>(),
                              Arg.Any<CancellationToken>())
                .Returns([DirectoryPackagingFixtures.Catalog()]);

        var exporter = new LibraryExporter(libraries,
                                           profiles,
                                           indexes,
                                           excluded,
                                           diffs,
                                           pages,
                                           chunks,
                                           bm25,
                                           sources,
                                           catalogs,
                                           assignments);
        return new ExportFixture(exporter);
    }

    private static ExportFixture BuildLegacyWebExportFixture()
    {
        var libraries = Substitute.For<ILibraryRepository>();
        var profiles = Substitute.For<ILibraryProfileRepository>();
        var indexes = Substitute.For<ILibraryIndexRepository>();
        var excluded = Substitute.For<IExcludedSymbolsRepository>();
        var diffs = Substitute.For<IDiffRepository>();
        var pages = Substitute.For<IPageRepository>();
        var chunks = Substitute.For<IChunkRepository>();
        var bm25 = EmptyBm25();
        var sources = Substitute.For<ISourceDocumentRepository>();
        var catalogs = Substitute.For<ISubjectCatalogRepository>();
        var assignments = Substitute.For<ISubjectAssignmentRepository>();
        LibraryRecord library = PackagingFixtures.MakeLibrary(LegacyLibraryId, LegacyVersion);
        LibraryVersionRecord version = PackagingFixtures.MakeVersion(LegacyLibraryId,
                                                                     LegacyVersion,
                                                                     pageCount: 1,
                                                                     chunkCount: 1,
                                                                     dim: DirectoryPackagingFixtures.EmbeddingDimensions,
                                                                     modelName:
                                                                     DirectoryPackagingFixtures.EmbeddingModelName) with
                                           {
                                               Id = $"{LegacyLibraryId}/{LegacyVersion}",
                                               EmbeddingProviderId = DirectoryPackagingFixtures.EmbeddingProviderId
                                           };
        IReadOnlyList<PageRecord> webPages = PackagingFixtures.MakePages(LegacyLibraryId,
                                                                          LegacyVersion,
                                                                          count: 1);
        IReadOnlyList<DocChunk> webChunks = PackagingFixtures.MakeChunks(LegacyLibraryId,
                                                                         LegacyVersion,
                                                                         count: 1,
                                                                         dim:
                                                                         DirectoryPackagingFixtures.EmbeddingDimensions);
        libraries.GetLibraryAsync(LegacyLibraryId, Arg.Any<CancellationToken>()).Returns(library);
        libraries.GetVersionAsync(LegacyLibraryId, LegacyVersion, Arg.Any<CancellationToken>()).Returns(version);
        pages.GetPagesAsync(LegacyLibraryId, LegacyVersion, Arg.Any<CancellationToken>()).Returns(webPages);
        chunks.GetChunksAsync(LegacyLibraryId, LegacyVersion, Arg.Any<CancellationToken>()).Returns(webChunks);
        profiles.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((LibraryProfile?) null);
        indexes.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns((LibraryIndex?) null);
        excluded.ListAsync(Arg.Any<string>(),
                           Arg.Any<string>(),
                           Arg.Any<SymbolRejectionReason?>(),
                           Arg.Any<int>(),
                           Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ExcludedSymbol>());
        sources.GetDirectoryDefinitionAsync(LegacyLibraryId, Arg.Any<CancellationToken>())
               .Returns((DirectoryLibraryDefinition?) null);
        sources.GetRevisionsAsync(LegacyLibraryId, LegacyVersion, Arg.Any<CancellationToken>())
               .Returns(Array.Empty<DocumentRevisionRecord>());
        catalogs.GetManyAsync(Arg.Any<IReadOnlyCollection<SubjectCatalogKey>>(),
                              Arg.Any<CancellationToken>())
                .Returns(Array.Empty<SubjectCatalogRecord>());
        assignments.GetByDocumentRevisionIdsAsync(Arg.Any<IReadOnlyCollection<string>>(),
                                                   Arg.Any<CancellationToken>())
                   .Returns(Array.Empty<SubjectAssignmentRecord>());
        return new ExportFixture(new LibraryExporter(libraries,
                                                      profiles,
                                                      indexes,
                                                      excluded,
                                                      diffs,
                                                      pages,
                                                      chunks,
                                                      bm25,
                                                      sources,
                                                      catalogs,
                                                      assignments));
    }

    private static ImportFixture BuildImportFixture()
    {
        var libraries = Substitute.For<ILibraryRepository>();
        var jobs = Substitute.For<IJobRepository>();
        var embedding = Substitute.For<IEmbeddingProvider>();
        var profiles = Substitute.For<ILibraryProfileRepository>();
        var indexes = Substitute.For<ILibraryIndexRepository>();
        var excluded = Substitute.For<IExcludedSymbolsRepository>();
        var diffs = Substitute.For<IDiffRepository>();
        var pages = Substitute.For<IPageRepository>();
        var chunks = Substitute.For<IChunkRepository>();
        var bm25 = EmptyBm25();
        var sources = Substitute.For<ISourceDocumentRepository>();
        var catalogs = Substitute.For<ISubjectCatalogRepository>();
        var assignments = Substitute.For<ISubjectAssignmentRepository>();
        libraries.GetLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns((LibraryRecord?) null);
        jobs.ListActiveAsync(Arg.Any<string>(),
                             Arg.Any<string?>(),
                             Arg.Any<JobType?>(),
                             Arg.Any<CancellationToken>())
            .Returns(Array.Empty<JobRecord>());
        embedding.ProviderId.Returns(DirectoryPackagingFixtures.EmbeddingProviderId);
        embedding.ModelName.Returns(DirectoryPackagingFixtures.EmbeddingModelName);
        embedding.Dimensions.Returns(DirectoryPackagingFixtures.EmbeddingDimensions);
        var importer = new LibraryImporter(libraries,
                                           jobs,
                                           embedding,
                                           profiles,
                                           indexes,
                                           excluded,
                                           diffs,
                                           pages,
                                           chunks,
                                           bm25,
                                           sources,
                                           catalogs,
                                           assignments);
        return new ImportFixture(importer, pages, chunks, sources, catalogs, assignments);
    }

    private static IBm25ShardRepository EmptyBm25()
    {
        var repository = Substitute.For<IBm25ShardRepository>();
        repository.GetAllShardsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(Array.Empty<Bm25Shard>());
        return repository;
    }

    private static void RewriteAsLegacyV1(string sourcePath, string destinationPath)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (ZipArchive source = ZipFile.OpenRead(sourcePath))
        {
            foreach(ZipArchiveEntry entry in source.Entries)
            {
                using Stream input = entry.Open();
                using var copy = new MemoryStream();
                input.CopyTo(copy);
                entries[entry.FullName] = copy.ToArray();
            }
        }

        JsonObject manifest = JsonNode.Parse(entries[BundlePaths.ManifestFile])!.AsObject();
        manifest["manifestVersion"] = 1;
        manifest.Remove("directory");
        RemoveV2EntriesAndManifestReferences(entries, manifest);
        entries[BundlePaths.ManifestFile] = JsonSerializer.SerializeToUtf8Bytes(manifest,
                                                                                BundleJsonOptions.Default);
        using ZipArchive destination = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
        foreach((string path, byte[] bytes) in entries.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            ZipArchiveEntry entry = destination.CreateEntry(path, CompressionLevel.NoCompression);
            using Stream output = entry.Open();
            output.Write(bytes);
        }
    }

    private static void RemoveV2EntriesAndManifestReferences(IDictionary<string, byte[]> entries,
                                                              JsonObject manifest)
    {
        string[] v2Prefixes = ["documents/", "subjects/", "document-artifacts/"];
        string[] versionV2Names = [DocumentRevisionsFile, SubjectAssignmentsFile];
        foreach(string path in entries.Keys.Where(path => v2Prefixes.Any(prefix =>
                                                       path.StartsWith(prefix, StringComparison.Ordinal)) ||
                                                   versionV2Names.Any(path.EndsWith))
                                           .ToArray())
            entries.Remove(path);
        if (manifest["blobs"] is JsonObject topLevel)
        {
            foreach(string property in topLevel.Select(item => item.Key)
                                               .Where(path => v2Prefixes.Any(prefix =>
                                                                  path.StartsWith(prefix,
                                                                                  StringComparison.Ordinal)))
                                               .ToArray())
                topLevel.Remove(property);
        }

        foreach(JsonObject version in manifest["versions"]!.AsArray().Select(node => node!.AsObject()))
        {
            version.Remove("sourceDocumentCount");
            version.Remove("documentRevisionCount");
            version.Remove("subjectAssignmentCount");
            if (version["blobs"] is JsonObject versionBlobs)
            {
                foreach(string property in versionBlobs.Select(item => item.Key)
                                                       .Where(path => versionV2Names.Any(path.EndsWith))
                                                       .ToArray())
                    versionBlobs.Remove(property);
            }
        }
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(ZipArchive archive, string path)
    {
        byte[] bytes = await ReadEntryBytesAsync(archive, path);
        return JsonNode.Parse(bytes)!.AsObject();
    }

    private static async Task<T> ReadSingleJsonlAsync<T>(ZipArchive archive, string path)
    {
        byte[] bytes = await ReadEntryBytesAsync(archive, path);
        string[] lines = Encoding.UTF8.GetString(bytes)
                                 .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        string line = Assert.Single(lines);
        return Assert.IsType<T>(JsonSerializer.Deserialize<T>(line, BundleJsonOptions.JsonlDefault));
    }

    private static async Task<byte[]> ReadEntryBytesAsync(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = archive.GetEntry(path)
                                ?? throw new Xunit.Sdk.XunitException($"Missing bundle entry '{path}'.");
        await using Stream input = entry.Open();
        using var copy = new MemoryStream();
        await input.CopyToAsync(copy, TestContext.Current.CancellationToken);
        return copy.ToArray();
    }

    private static async Task AssertArchiveDoesNotContainRootAsync(ZipArchive archive, string root)
    {
        foreach(ZipArchiveEntry entry in archive.Entries)
        {
            byte[] bytes = await ReadEntryBytesAsync(archive, entry.FullName);
            string normalized = Encoding.UTF8.GetString(bytes).Replace("\\\\", "\\", StringComparison.Ordinal);
            Assert.DoesNotContain(root, entry.FullName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(root, normalized, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string ArtifactPath(string hash) => $"document-artifacts/{hash}.bin";

    private static string VersionPath(string fileName) =>
        BundlePaths.VersionFilePath(DirectoryPackagingFixtures.Version, fileName);

    private sealed record ExportFixture(LibraryExporter Exporter);

    private sealed record ImportFixture(LibraryImporter Importer,
                                        IPageRepository Pages,
                                        IChunkRepository Chunks,
                                        ISourceDocumentRepository Sources,
                                        ISubjectCatalogRepository Catalogs,
                                        ISubjectAssignmentRepository Assignments);

    private const string SourcesPath = "documents/sources.jsonl";
    private const string SubjectCatalogsPath = "subjects/catalogs.jsonl";
    private const string DocumentRevisionsFile = "documentRevisions.jsonl";
    private const string SubjectAssignmentsFile = "subjectAssignments.jsonl";
    private const string LegacyLibraryId = "stage7-legacy-web";
    private const string LegacyVersion = "1.0";
}
