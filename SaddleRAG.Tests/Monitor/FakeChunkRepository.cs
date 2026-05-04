// FakeChunkRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Tests.Monitor;

internal sealed class FakeChunkRepository : IChunkRepository
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, int>> mHostMaps = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, double>> mLangMaps = new();

    public void SetHosts(string libraryId, string version, IReadOnlyDictionary<string, int> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        mHostMaps[Key(libraryId, version)] = hosts;
    }

    public void SetLanguages(string libraryId, string version, IReadOnlyDictionary<string, double> langs)
    {
        ArgumentNullException.ThrowIfNull(langs);
        mLangMaps[Key(libraryId, version)] = langs;
    }

    public Task<IReadOnlyDictionary<string, int>> GetHostnameDistributionAsync(string libraryId,
                                                                               string version,
                                                                               CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, int> result = mHostMaps.GetValueOrDefault(Key(libraryId, version))
                                                  ?? new Dictionary<string, int>();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<string, double>> GetLanguageMixAsync(string libraryId,
                                                                         string version,
                                                                         CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, double> result = mLangMaps.GetValueOrDefault(Key(libraryId, version))
                                                     ?? new Dictionary<string, double>();
        return Task.FromResult(result);
    }

    public Task InsertChunksAsync(IReadOnlyList<DocChunk> chunks, CancellationToken ct = default) =>
        throw new NotSupportedException("FakeChunkRepository: InsertChunksAsync not supported in this test");

    public Task UpsertChunksAsync(IReadOnlyList<DocChunk> chunks, CancellationToken ct = default) =>
        throw new NotSupportedException("FakeChunkRepository: UpsertChunksAsync not supported in this test");

    public Task<long> DeleteChunksAsync(string libraryId, string version, CancellationToken ct = default) =>
        throw new NotSupportedException("FakeChunkRepository: DeleteChunksAsync not supported in this test");

    public Task<IReadOnlyList<DocChunk>> GetChunksAsync(string libraryId,
                                                        string version,
                                                        CancellationToken ct = default) =>
        throw new NotSupportedException("FakeChunkRepository: GetChunksAsync not supported in this test");

    public Task<int> GetChunkCountAsync(string libraryId, string version, CancellationToken ct = default) =>
        throw new NotSupportedException("FakeChunkRepository: GetChunkCountAsync not supported in this test");

    public Task<IReadOnlyList<DocChunk>> FindByQualifiedNameAsync(string libraryId,
                                                                  string version,
                                                                  string qualifiedName,
                                                                  CancellationToken ct = default) =>
        throw new NotSupportedException("FakeChunkRepository: FindByQualifiedNameAsync not supported in this test");

    public Task<IReadOnlyList<string>> GetQualifiedNamesAsync(string libraryId,
                                                              string version,
                                                              string? filter = null,
                                                              CancellationToken ct = default) =>
        throw new NotSupportedException("FakeChunkRepository: GetQualifiedNamesAsync not supported in this test");

    public Task<IReadOnlyList<string>> GetSymbolsAsync(string libraryId,
                                                       string version,
                                                       SymbolKind kind,
                                                       string? filter = null,
                                                       CancellationToken ct = default) =>
        throw new NotSupportedException("FakeChunkRepository: GetSymbolsAsync not supported in this test");

    public Task<IReadOnlyList<Symbol>> GetAllSymbolsAsync(string libraryId,
                                                          string version,
                                                          string? filter = null,
                                                          CancellationToken ct = default) =>
        throw new NotSupportedException("FakeChunkRepository: GetAllSymbolsAsync not supported in this test");

    public Task<long> UpdateCategoryByPageUrlAsync(string libraryId,
                                                   string version,
                                                   string pageUrl,
                                                   DocCategory newCategory,
                                                   CancellationToken ct = default) =>
        throw new NotSupportedException("FakeChunkRepository: UpdateCategoryByPageUrlAsync not supported in this test");

    public Task<bool> HasStaleChunksAsync(string libraryId,
                                          string version,
                                          int currentParserVersion,
                                          CancellationToken ct = default) =>
        throw new NotSupportedException("FakeChunkRepository: HasStaleChunksAsync not supported in this test");

    public Task<IReadOnlyList<string>> GetSampleTitlesAsync(string libraryId,
                                                            string version,
                                                            int limit,
                                                            CancellationToken ct = default) =>
        throw new NotSupportedException("FakeChunkRepository: GetSampleTitlesAsync not supported in this test");

    private static string Key(string libraryId, string version) => $"{libraryId}/{version}";
}
