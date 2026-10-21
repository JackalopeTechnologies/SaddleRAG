// DependencyHealthMonitor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using SaddleRAG.Core;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Documents.Docling;
using SaddleRAG.Ingestion.Embedding;

namespace SaddleRAG.Mcp;

/// <summary>Continuously observes dependencies without keeping language models loaded.</summary>
public sealed class DependencyHealthMonitor(IServiceProvider services, DependencyHealthState state,
                                             ILogger<DependencyHealthMonitor> logger) : BackgroundService
{
    private const string DefaultProfile = "default";
    private const string OllamaName = "Ollama";
    private const string EmbeddingWork = "Search, indexing and classification";
    private const string ClassificationWork = "Classification";
    private const string OllamaRemediation = "Start the selected Ollama installation and check its configured address and model list.";
    private const string OllamaUnused = "Not selected for embeddings or classification.";
    private const string TagsPath = "/api/tags";
    private const string ModelsProperty = "models";
    private const string NameProperty = "name";
    private const string LatestTag = ":latest";
    private const string MissingModel = "A selected Ollama model is missing.";
    private const string OllamaReady = "Ollama responded and the selected models are installed.";
    private const string DoclingName = "Document conversion";
    private const string DoclingWork = "Document scanning";
    private const string DoclingRemediation = "Check the user-managed Docling service and its configured address; then retry document scanning.";
    private const string MongoPrefix = "MongoDB (";
    private const string MongoSuffix = ")";
    private const string MongoWork = "Stored libraries and jobs in ";
    private const string MongoRemediation = "Check the MongoDB service and connection settings. SaddleRAG reconnects automatically.";
    private const string MongoReady = "MongoDB accepted a database command.";
    private static readonly TimeSpan smCheckTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan smCheckInterval = TimeSpan.FromSeconds(10);
    private readonly HttpClient mHttp = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (DependencyProbe probe in CreateProbes())
                    await DependencyProbeRunner.CheckAsync(probe, state, smCheckTimeout, logger, stoppingToken);
                using var recoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                recoveryTimeout.CancelAfter(smCheckTimeout);
                try
                {
                    await services.GetRequiredService<InterruptedJobRecovery>().CheckAsync(recoveryTimeout.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Retain interrupted statuses for the next pass.
                }
                await Task.Delay(smCheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private IReadOnlyList<DependencyProbe> CreateProbes()
    {
        var probes = new List<DependencyProbe>();
        var factory = services.GetRequiredService<SaddleRagDbContextFactory>();
        string? defaultProfile = factory.GetDefaultProfileName();
        probes.Add(DatabaseProbe(factory, null, defaultProfile ?? DefaultProfile, true));
        foreach (string profile in factory.GetProfileNames().Where(name => name != defaultProfile))
            probes.Add(DatabaseProbe(factory, profile, profile, false));

        var native = services.GetRequiredService<IOptions<OnnxSettings>>().Value;
        var ollama = services.GetRequiredService<IOptions<OllamaSettings>>().Value;
        var classifier = services.GetRequiredService<ILlmClassifier>();
        bool embeddings = !native.Enabled || !native.EmbeddingEnabled;
        bool classification = ClassifierBackendSwitch.IsOllamaActive(classifier);
        probes.Add(new DependencyProbe(OllamaName, embeddings,
            embeddings ? EmbeddingWork : ClassificationWork,
            OllamaRemediation,
            token => CheckOllamaAsync(ollama, embeddings, classification, token)));

        probes.Add(new DependencyProbe(DoclingName, false, DoclingWork,
            DoclingRemediation, CheckDoclingAsync));
        return probes;
    }

    private async Task<string> CheckOllamaAsync(OllamaSettings ollama, bool embeddings,
                                                bool classification, CancellationToken token)
    {
                string detail = OllamaUnused;
                if (embeddings || classification)
                {
                    using HttpResponseMessage response = await mHttp.GetAsync(
                        new Uri(new Uri(ollama.Endpoint), TagsPath), token);
                    response.EnsureSuccessStatusCode();
                    using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    var models = json.RootElement.GetProperty(ModelsProperty).EnumerateArray()
                        .Select(model => model.GetProperty(NameProperty).GetString()).ToHashSet(StringComparer.Ordinal);
                    var required = new List<string>();
                    if (embeddings)
                        required.Add(ollama.EmbeddingModel);
                    if (classification)
                        required.Add(ollama.GetActiveClassificationModel().Name);
                    foreach (string model in required)
                    {
                        string name = model.Contains(':') ? model : model + LatestTag;
                        if (!models.Contains(name))
                            throw new InvalidOperationException(MissingModel);
                    }
                    detail = OllamaReady;
                }
                return detail;
    }

    private async Task<string> CheckDoclingAsync(CancellationToken token)
    {
                var client = services.GetRequiredService<IDoclingClient>();
                DoclingServiceObservation observation = await client.CheckReadinessAsync(token);
                services.GetRequiredService<IDoclingCapabilityService>().RecordServiceObservation(observation);
                if (!observation.Succeeded)
                    throw new InvalidOperationException(observation.ReasonCode);
                return observation.Detail;
    }

    private static DependencyProbe DatabaseProbe(SaddleRagDbContextFactory factory, string? profile,
                                                   string displayName, bool required) =>
        new(MongoPrefix + displayName + MongoSuffix, required, MongoWork + displayName,
            MongoRemediation,
            async token =>
            {
                SaddleRagDbContext context = factory.GetForProfile(profile);
                await context.CheckHealthAsync(token);
                return MongoReady;
            });

    public override void Dispose()
    {
        mHttp.Dispose();
        base.Dispose();
    }
}
