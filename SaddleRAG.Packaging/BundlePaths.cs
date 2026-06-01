// BundlePaths.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Packaging;

/// <summary>
///     Canonical paths and filenames inside a SaddleRAG bundle (`.srlib.zip`).
///     One place to change if the on-disk layout ever evolves.
/// </summary>
public static class BundlePaths
{
    public const int CurrentManifestVersion = 1;

    public const string ManifestFile = "manifest.json";
    public const string LibraryFile = "library.json";
    public const string VersionsDir = "versions";
    public const string VersionFile = "version.json";
    public const string ProfileFile = "profile.json";
    public const string IndexFile = "index.json";
    public const string ExcludedSymbolsFile = "excludedSymbols.jsonl";
    public const string VersionDiffFile = "versionDiff.json";
    public const string PagesFile = "pages.jsonl";
    public const string ChunksFile = "chunks.jsonl";
    public const string EmbeddingsBlobFile = "chunks.embeddings.f32";
    public const string Bm25Dir = "bm25";
    public const string Bm25ShardsFile = "bm25/shards.jsonl";
    public const string Bm25GridFsDir = "bm25/gridfs";

    public static string VersionDir(string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        return $"{VersionsDir}/{version}";
    }

    public static string VersionFilePath(string version, string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        return $"{VersionDir(version)}/{fileName}";
    }

    public static string Bm25GridFsBlob(string version, string gridFsId)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(gridFsId);
        return $"{VersionDir(version)}/{Bm25GridFsDir}/{gridFsId}.bin";
    }
}
