// OllamaEmbeddingProvider.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>Embeds ordered batches through Ollama with bounded model retention.</summary>
public class OllamaEmbeddingProvider : IEmbeddingProvider
{
    public OllamaEmbeddingProvider(IOptions<OllamaSettings> settings,
                                   ILogger<OllamaEmbeddingProvider> logger)
        : this(settings, logger, CreateClient(settings))
    {
    }

    /// <summary>Allows request-level testing without a running Ollama server.</summary>
    internal OllamaEmbeddingProvider(IOptions<OllamaSettings> settings,
                                     ILogger<OllamaEmbeddingProvider> logger,
                                     OllamaApiClient client)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(client);
        mSettings = settings.Value;
        ValidateSettings(mSettings);
        mLogger = logger;
        mClient = client;
    }

    /// <inheritdoc />
    public string ProviderId => ProviderIdName;

    /// <inheritdoc />
    public string ModelName => mSettings.EmbeddingModel;

    /// <inheritdoc />
    public int Dimensions => mSettings.EmbeddingDimensions;

    /// <inheritdoc />
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts,
                                            EmbedRole role = EmbedRole.Document,
                                            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ct.ThrowIfCancellationRequested();
        string prefix = SelectPrefix(role);
        // Validate the entire call before sending any part of it to Ollama.
        foreach (string text in texts)
            ArgumentNullException.ThrowIfNull(text);

        var result = new float[texts.Count][];
        for (var offset = 0; offset < texts.Count; offset += mSettings.EmbeddingBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(mSettings.EmbeddingBatchSize, texts.Count - offset);
            var input = new string[count];
            for (var index = 0; index < count; index++)
                input[index] = prefix + texts[offset + index];

            float[][] batch = await EmbedBatchWithRetryAsync(input, ct);
            Array.Copy(batch, sourceIndex: 0, result, offset, count);
        }

        if (texts.Count > 0)
            mLogger.LogInformation("Embedded {Count} texts via Ollama ({Model}) as {Role}",
                                   texts.Count, ModelName, role);
        return result;
    }

    private string SelectPrefix(EmbedRole role)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));

        var result = string.Empty;
        if (mSettings.EmbeddingUseTaskPrefixes)
        {
            string family = ModelName.Split(':')[0];
            result = (family, role) switch
            {
                ("nomic-embed-text", EmbedRole.Document) => NomicDocumentPrefix,
                ("nomic-embed-text", EmbedRole.Query) => NomicQueryPrefix,
                ("embeddinggemma" or "embeddinggemma-2", EmbedRole.Document) => GemmaDocumentPrefix,
                ("embeddinggemma" or "embeddinggemma-2", EmbedRole.Query) => GemmaQueryPrefix,
                _ => throw new InvalidOperationException(
                    $"No task instructions are defined for Ollama embedding model '{ModelName}'. " +
                    UnsupportedTaskInstructionsAdvice)
            };
        }
        return result;
    }

    private async Task<float[][]> EmbedBatchWithRetryAsync(string[] input, CancellationToken ct)
    {
        float[][] result = [];
        var succeeded = false;
        for (var attempt = 0; attempt < MaxRetryAttempts && !succeeded; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (attempt > 0)
                    await Task.Delay(InitialRetryDelayMs * (1 << (attempt - 1)), ct);

                EmbedResponse response = await mClient.EmbedAsync(new EmbedRequest
                {
                    Model = ModelName,
                    Input = [..input],
                    KeepAlive = mSettings.EmbeddingKeepAliveSeconds.ToString(CultureInfo.InvariantCulture) + "s",
                    // Reject oversize inputs; silently dropping content damages retrieval.
                    Truncate = false,
                    Options = new RequestOptions { NumThread = mSettings.EmbeddingThreads }
                }, ct);
                result = ValidateResponse(response, input.Length);
                succeeded = true;
            }
            catch (Exception ex) when (attempt < MaxRetryAttempts - 1 && IsTransient(ex, ct))
            {
                mLogger.LogWarning(ex, "Ollama embedding attempt {Attempt} failed; retrying", attempt + 1);
            }
        }
        return result;
    }

    private float[][] ValidateResponse(EmbedResponse response, int expectedCount)
    {
        if (response.Embeddings == null || response.Embeddings.Count != expectedCount)
            throw new InvalidDataException($"Ollama returned {response.Embeddings?.Count ?? 0} embeddings; expected {expectedCount}.");

        var result = new float[expectedCount][];
        for (var index = 0; index < expectedCount; index++)
        {
            float[]? vector = response.Embeddings[index];
            if (vector == null || vector.Length != Dimensions)
                throw new InvalidDataException($"Ollama embedding {index} has {vector?.Length ?? 0} dimensions; expected {Dimensions}.");
            if (vector.Any(value => !float.IsFinite(value)) || !vector.Any(value => value != 0))
                throw new InvalidDataException($"Ollama embedding {index} contains non-finite values or has zero length.");
            result[index] = vector;
        }
        return result;
    }

    private static bool IsTransient(Exception error, CancellationToken ct)
    {
        bool result = !ct.IsCancellationRequested && error switch
        {
            HttpRequestException request => request.StatusCode is null or HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests || (int)request.StatusCode >= (int)HttpStatusCode.InternalServerError,
            TaskCanceledException => true,
            _ => false
        };
        return result;
    }

    private static OllamaApiClient CreateClient(IOptions<OllamaSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateSettings(settings.Value);
        return new OllamaApiClient(new Uri(settings.Value.Endpoint));
    }

    private static void ValidateSettings(OllamaSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.EmbeddingModel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.EmbeddingDimensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.EmbeddingBatchSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.EmbeddingBatchSize, OllamaSettings.MaximumEmbeddingBatchSize);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.EmbeddingKeepAliveSeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.EmbeddingKeepAliveSeconds, OllamaSettings.MaximumEmbeddingKeepAliveSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.EmbeddingThreads);
    }

    /// <summary>Identifier persisted with Ollama-embedded library versions.</summary>
    public const string ProviderIdName = "ollama";
    private const string NomicDocumentPrefix = "search_document: ";
    private const string UnsupportedTaskInstructionsAdvice =
        "Choose a supported model or disable EmbeddingUseTaskPrefixes for unprefixed embeddings.";
    private const string NomicQueryPrefix = "search_query: ";
    private const string GemmaDocumentPrefix = "title: none | text: ";
    private const string GemmaQueryPrefix = "task: search result | query: ";
    private const int MaxRetryAttempts = 3;
    private const int InitialRetryDelayMs = 1000;
    private readonly OllamaApiClient mClient;
    private readonly ILogger<OllamaEmbeddingProvider> mLogger;
    private readonly OllamaSettings mSettings;
}
