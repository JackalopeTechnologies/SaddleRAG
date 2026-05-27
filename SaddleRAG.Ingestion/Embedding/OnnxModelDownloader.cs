// OnnxModelDownloader.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Ensures the ONNX model files referenced by the active
///     <see cref="OnnxSettings" /> entries are present on disk under
///     <see cref="OnnxSettings.ModelsDir" />. Idempotent: files already
///     present are left alone. Downloads happen via the HuggingFace
///     resolve endpoint to a <c>.tmp</c> file and are atomically renamed
///     on success.
///     Invoked by the MSI <c>--prewarm</c> custom action so first install
///     populates the model directory without manual user action.
/// </summary>
public class OnnxModelDownloader
{
    public OnnxModelDownloader(IHttpClientFactory httpClientFactory,
                               IOptions<OnnxSettings> settings,
                               ILogger<OnnxModelDownloader> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        mHttpClientFactory = httpClientFactory;
        mSettings = settings.Value;
        mLogger = logger;
    }

    private readonly IHttpClientFactory mHttpClientFactory;
    private readonly ILogger<OnnxModelDownloader> mLogger;
    private readonly OnnxSettings mSettings;

    /// <summary>
    ///     Downloads the active embedding model (if <c>EmbeddingEnabled</c>)
    ///     and the active reranker model (if one resolves) into
    ///     <see cref="OnnxSettings.ModelsDir" />. Files already present
    ///     and non-empty are skipped.
    /// </summary>
    public async Task EnsureActiveModelsAsync(CancellationToken ct)
    {
        if (!mSettings.Enabled)
            mLogger.LogInformation("OnnxModelDownloader skipped (Onnx.Enabled=false).");
        else
        {
            Directory.CreateDirectory(mSettings.ModelsDir);

            if (mSettings.EmbeddingEnabled)
            {
                var embeddingEntry = mSettings.GetActiveEmbeddingModel();
                await EnsureEmbeddingModelAsync(embeddingEntry, ct);
            }

            var rerankerEntry = mSettings.GetActiveRerankerModel();
            if (rerankerEntry != null)
                await EnsureRerankerModelAsync(rerankerEntry, ct);
        }
    }

    /// <summary>Downloads a single embedding model entry into its target directory.</summary>
    public async Task EnsureEmbeddingModelAsync(EmbeddingModelEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string modelDir = Path.Combine(mSettings.ModelsDir, entry.Name);
        Directory.CreateDirectory(modelDir);

        await DownloadIfMissingAsync(entry.RepoId, entry.ModelFile,
                                     Path.Combine(modelDir, ModelOnnxFileName), ct
                                    );

        await DownloadTokenizerAsync(entry.TokenizerFamily, entry.VocabFile, entry.SpmFile,
                                     entry.RepoId, modelDir, entry.Name, ct
                                    );
    }

    /// <summary>Downloads a single reranker model entry into its target directory.</summary>
    public async Task EnsureRerankerModelAsync(RerankerModelEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string modelDir = Path.Combine(mSettings.ModelsDir, entry.Name);
        Directory.CreateDirectory(modelDir);

        await DownloadIfMissingAsync(entry.RepoId, entry.ModelFile,
                                     Path.Combine(modelDir, ModelOnnxFileName), ct
                                    );

        await DownloadTokenizerAsync(entry.TokenizerFamily, entry.VocabFile, entry.SpmFile,
                                     entry.RepoId, modelDir, entry.Name, ct
                                    );
    }

    private async Task DownloadTokenizerAsync(TokenizerFamily family,
                                              string vocabFile,
                                              string spmFile,
                                              string repoId,
                                              string modelDir,
                                              string entryName,
                                              CancellationToken ct)
    {
        switch (family)
        {
            case TokenizerFamily.Bert:
                if (string.IsNullOrEmpty(vocabFile))
                    throw new InvalidOperationException(
                        $"Onnx entry '{entryName}' has TokenizerFamily=Bert but no VocabFile specified."
                    );
                await DownloadIfMissingAsync(repoId, vocabFile,
                                             Path.Combine(modelDir, VocabTxtFileName), ct
                                            );
                break;
            case TokenizerFamily.SentencePiece:
                if (string.IsNullOrEmpty(spmFile))
                    throw new InvalidOperationException(
                        $"Onnx entry '{entryName}' has TokenizerFamily=SentencePiece but no SpmFile specified."
                    );
                await DownloadIfMissingAsync(repoId, spmFile,
                                             Path.Combine(modelDir, SpmModelFileName), ct
                                            );
                break;
            default:
                throw new InvalidOperationException(
                    $"Onnx entry '{entryName}' has unknown TokenizerFamily '{family}'."
                );
        }
    }

    private async Task DownloadIfMissingAsync(string repoId, string sourcePath, string destPath, CancellationToken ct)
    {
        bool alreadyPresent = File.Exists(destPath) && new FileInfo(destPath).Length > 0;
        if (alreadyPresent)
            mLogger.LogDebug("Skipping download (already present): {Dest}", destPath);
        else
            await PerformDownloadAsync(repoId, sourcePath, destPath, ct);
    }

    private async Task PerformDownloadAsync(string repoId,
                                            string sourcePath,
                                            string destPath,
                                            CancellationToken ct)
    {
        string url = $"{HuggingFaceBase}/{repoId}/{HuggingFaceResolveSegment}/{sourcePath}";
        string tmpPath = destPath + TempSuffix;

        mLogger.LogInformation("Downloading {Url} -> {Dest}", url, destPath);
        try
        {
            await StreamResponseToTmpAsync(url, tmpPath, ct);
            EnsureNonEmpty(tmpPath, url);
            File.Move(tmpPath, destPath, overwrite: true);

            long sizeKb = new FileInfo(destPath).Length / BytesPerKb;
            mLogger.LogInformation("Downloaded {Dest} ({SizeKb} KB)", destPath, sizeKb);
        }
        finally
        {
            DeleteIfExists(tmpPath);
        }
    }

    private async Task StreamResponseToTmpAsync(string url, string tmpPath, CancellationToken ct)
    {
        var client = mHttpClientFactory.CreateClient(HttpClientName);
        using HttpResponseMessage response =
            await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using Stream src = await response.Content.ReadAsStreamAsync(ct);
        await using FileStream dst = File.Create(tmpPath);
        await src.CopyToAsync(dst, ct);
    }

    private static void EnsureNonEmpty(string tmpPath, string url)
    {
        long downloaded = new FileInfo(tmpPath).Length;
        if (downloaded == 0)
            throw new InvalidOperationException(string.Format(EmptyResponseFormat, url));
    }

    private void DeleteIfExists(string tmpPath)
    {
        if (File.Exists(tmpPath))
        {
            // Widened from IOException-only to also cover the Windows reality
            // that File.Delete can throw UnauthorizedAccessException (AV
            // quarantine stripping delete rights, read-only attribute, ACL
            // denial) — a narrower catch would let that propagate out of the
            // enclosing try/finally and mask the original download failure.
            try
            {
                File.Delete(tmpPath);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException
                                        or System.Security.SecurityException)
            {
                mLogger.LogWarning(ex, "Failed to delete temp download file {TmpPath}; this may leave an orphan that the next download will retry past.", tmpPath);
            }
        }
    }

    public const string HttpClientName = "OnnxModelDownloader";

    private const string EmptyResponseFormat = "Download from '{0}' returned a 200 OK but the response body was empty. Refusing to write a zero-byte model file. Verify the RepoId/ModelFile combination resolves on HuggingFace.";

    private const string HuggingFaceBase = "https://huggingface.co";
    private const string HuggingFaceResolveSegment = "resolve/main";
    private const string ModelOnnxFileName = "model.onnx";
    private const string VocabTxtFileName = "vocab.txt";
    private const string SpmModelFileName = "spm.model";
    private const string TempSuffix = ".tmp";
    private const int BytesPerKb = 1024;
}
