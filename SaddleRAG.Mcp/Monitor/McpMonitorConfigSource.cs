// McpMonitorConfigSource.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Options;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Mcp.Monitor;

/// <summary>
///     Host-side <see cref="IMonitorConfigSource" /> implementation for the
///     Monitor /config page (issue #73). Lives in the MCP project so the
///     SaddleRAG.Monitor assembly can keep its Core-only project-reference
///     footprint while still surfacing settings + runtime-capability
///     objects that belong to the Ingestion + Database layers.
/// </summary>
internal sealed class McpMonitorConfigSource : IMonitorConfigSource
{
    public McpMonitorConfigSource(IOptions<OnnxSettings> onnx,
                                  IOptions<OllamaSettings> ollama,
                                  IOptions<SaddleRagDbSettings> mongo,
                                  IOptions<RankingSettings> ranking,
                                  OnnxRuntimeCapabilities capabilities,
                                  IEmbeddingProvider embeddingProvider,
                                  ClassifierBackendSwitch classifierSwitch)
    {
        ArgumentNullException.ThrowIfNull(onnx);
        ArgumentNullException.ThrowIfNull(ollama);
        ArgumentNullException.ThrowIfNull(mongo);
        ArgumentNullException.ThrowIfNull(ranking);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(classifierSwitch);
        mOnnx = onnx;
        mOllama = ollama;
        mMongo = mongo;
        mRanking = ranking;
        mCapabilities = capabilities;
        mEmbeddingProvider = embeddingProvider;
        mClassifierSwitch = classifierSwitch;
    }

    private readonly OnnxRuntimeCapabilities mCapabilities;
    private readonly ClassifierBackendSwitch mClassifierSwitch;
    private readonly IEmbeddingProvider mEmbeddingProvider;
    private readonly IOptions<SaddleRagDbSettings> mMongo;
    private readonly IOptions<OllamaSettings> mOllama;
    private readonly IOptions<OnnxSettings> mOnnx;
    private readonly IOptions<RankingSettings> mRanking;

    /// <inheritdoc />
    public MonitorConfigSnapshot GetSnapshot()
    {
        var onnx = mOnnx.Value;
        var ollama = mOllama.Value;
        var mongo = mMongo.Value;
        var ranking = mRanking.Value;

        // Clamped overload so the page reports the variant the service actually
    // loads when the configured EP is not compiled into this build (#135).
    ClassifierModelEntry classifierEntry = ClassifierEntryResolver.Resolve(onnx,
                                                                           onnx.ExecutionProvider,
                                                                           mCapabilities.CompiledInProviders);
        string classifierModelDir = Path.Combine(onnx.ModelsDir, classifierEntry.Name);
        bool classifierFilesPresent = Directory.Exists(classifierModelDir);
        var classifier = new MonitorConfigClassifier(ActiveBackend: mClassifierSwitch.ActiveBackendName,
                                                     ActiveOnnxModel: classifierEntry.Name,
                                                     RepoId: classifierEntry.RepoId,
                                                     ModelFolder: classifierEntry.ModelFolder,
                                                     ModelFilesPresent: classifierFilesPresent,
                                                     OllamaClassificationModel: ollama.ActiveClassificationModel
                                                    );

        var embedding = new MonitorConfigEmbedding(ProviderId: mEmbeddingProvider.ProviderId,
                                                   ModelName: mEmbeddingProvider.ModelName,
                                                   Dimensions: mEmbeddingProvider.Dimensions,
                                                   OnnxBacked: onnx is { Enabled: true, EmbeddingEnabled: true },
                                                   OnnxEmbeddingEnabled: onnx.EmbeddingEnabled
                                                  );

        string? activeReranker =
            string.IsNullOrWhiteSpace(onnx.ActiveRerankerModel)
            || string.Equals(onnx.ActiveRerankerModel,
                             OnnxSettings.RerankerNoneSentinel,
                             StringComparison.OrdinalIgnoreCase
                            )
                ? null
                : onnx.ActiveRerankerModel;
        var reranker = new MonitorConfigReranker(Strategy: ranking.ReRankerStrategy.ToString(),
                                                 ActiveModel: activeReranker,
                                                 OnnxEnabled: onnx.Enabled
                                                );

        var requestedEp = mCapabilities.RequestedProvider.ToString();
        var activeEp = mCapabilities.ActiveProvider.ToString();
        var executionProvider = new MonitorConfigExecutionProvider(Requested: requestedEp,
                                                                   Active: activeEp,
                                                                   MatchesRequested: requestedEp == activeEp,
                                                                   CompiledInProviders: mCapabilities
                                                                       .CompiledInProviders.Select(p => p.ToString())
                                                                       .ToList(),
                                                                   LastLoadWarning: mCapabilities.LastLoadWarning
                                                                  );

        (var connectionString, var databaseName) = mongo.Resolve();
        var mongoCard = new MonitorConfigMongo(ActiveProfileName: ResolveActiveProfileName(mongo),
                                               Host: MaskMongoConnectionString(connectionString),
                                               DatabaseName: databaseName,
                                               CredentialsPresent: ContainsCredentials(connectionString)
                                              );

        var ollamaCard = new MonitorConfigOllama(Endpoint: ollama.Endpoint,
                                                 ClassificationModel: ollama.ActiveClassificationModel,
                                                 ReconModel: ollama.ActiveReconModel,
                                                 EmbeddingModel: ollama.EmbeddingModel
                                                );

        var profile = new MonitorConfigProfile(EffectiveProfile: ResolveActiveProfileName(mongo));

        return new MonitorConfigSnapshot(Classifier: classifier,
                                         Embedding: embedding,
                                         Reranker: reranker,
                                         ExecutionProvider: executionProvider,
                                         Mongo: mongoCard,
                                         Ollama: ollamaCard,
                                         Profile: profile
                                        );
    }

    /// <summary>
    ///     Mask the user:password portion of a MongoDB connection string so
    ///     the Monitor page can show the host + path without leaking
    ///     credentials. Examples:
    ///     <c>mongodb://user:secret@host:27017/db</c> -> <c>mongodb://***:***@host:27017/db</c>.
    ///     Exposed as <c>internal static</c> so the masking rule can be
    ///     unit-tested without standing up the full host.
    /// </summary>
    internal static string MaskMongoConnectionString(string connectionString)
    {
        string result = connectionString;
        if (!string.IsNullOrEmpty(connectionString))
        {
            int schemeEnd = connectionString.IndexOf(SchemeSeparator, StringComparison.Ordinal);
            int credentialEnd = connectionString.IndexOf(CredentialMarker, StringComparison.Ordinal);
            if (schemeEnd > 0 && credentialEnd > schemeEnd + SchemeSeparator.Length)
            {
                var scheme = connectionString[..(schemeEnd + SchemeSeparator.Length)];
                var afterAt = connectionString[(credentialEnd + 1)..];
                result = $"{scheme}{MaskedPlaceholder}{afterAt}";
            }
        }

        return result;
    }

    internal static bool ContainsCredentials(string connectionString)
    {
        var result = false;
        if (!string.IsNullOrEmpty(connectionString))
        {
            int schemeEnd = connectionString.IndexOf(SchemeSeparator, StringComparison.Ordinal);
            int credentialEnd = connectionString.IndexOf(CredentialMarker, StringComparison.Ordinal);
            if (schemeEnd > 0 && credentialEnd > schemeEnd + SchemeSeparator.Length)
                result = true;
        }

        return result;
    }

    private static string ResolveActiveProfileName(SaddleRagDbSettings settings)
    {
        var profileOverride = Environment.GetEnvironmentVariable(MongoProfileEnvVar);
        var profileName = profileOverride ?? settings.ActiveProfile;
        var result = string.IsNullOrWhiteSpace(profileName) ? DirectSettingsLabel : profileName;
        return result;
    }

    private const string SchemeSeparator = "://";
    private const string CredentialMarker = "@";
    private const string MaskedPlaceholder = "***:***@";
    private const string DirectSettingsLabel = "(direct)";
    private const string MongoProfileEnvVar = "SADDLERAG_MONGODB_PROFILE";
}
