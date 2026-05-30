// OllamaProbe.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     Lightweight Ollama health check and idempotent model-pull helper for
///     the optional Ollama classifier backend. Two responsibilities only:
///     report whether Ollama is reachable, and pull the configured model
///     when it is absent (never re-pulling a model that is already present).
///     Does NOT manage processes, installation, or warm keep-alive — those
///     concerns remain in <see cref="OllamaBootstrapper" /> until the DI
///     swap in the next task.
/// </summary>
public sealed class OllamaProbe
{
    /// <summary>
    ///     Initializes a new <see cref="OllamaProbe" />.
    /// </summary>
    /// <param name="http">
    ///     <see cref="HttpClient" /> whose <see cref="HttpClient.BaseAddress" />
    ///     should be set to the Ollama root endpoint (e.g.
    ///     <c>http://localhost:11434</c>), or whose requests will be routed
    ///     through whatever base address the caller configures. When
    ///     <paramref name="settings" /> is supplied the base address is
    ///     derived from <see cref="OllamaSettings.Endpoint" /> and any
    ///     base address already on the client is ignored.
    /// </param>
    /// <param name="settings">Ollama configuration (endpoint, etc.).</param>
    public OllamaProbe(HttpClient http, OllamaSettings settings)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(settings);

        mHttp = http;
        mSettings = settings;
    }

    private readonly HttpClient mHttp;
    private readonly OllamaSettings mSettings;

    /// <summary>
    ///     Returns <see langword="true" /> when Ollama answers a
    ///     <c>GET /api/tags</c> request with a success status code.
    ///     Returns <see langword="false" /> on any transport error or
    ///     non-success response. External cancellation propagates as
    ///     <see cref="OperationCanceledException" />.
    /// </summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        var tagsUri = BuildUri(TagsPath);
        bool result;
        try
        {
            var response = await mHttp.GetAsync(tagsUri, ct);
            result = response.IsSuccessStatusCode;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            result = false;
        }

        return result;
    }

    /// <summary>
    ///     Ensures <paramref name="model" /> is available locally in Ollama.
    ///     Calls <c>GET /api/tags</c> first; if the model is already present
    ///     the method returns immediately without pulling. Only when the
    ///     model is absent does it send <c>POST /api/pull</c>.
    ///     Throws <see cref="InvalidOperationException" /> with an
    ///     actionable message if Ollama is not reachable.
    /// </summary>
    /// <param name="model">The Ollama model name (e.g. <c>phi4-mini:3.8b</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task EnsureModelAsync(string model, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);

        var tagsUri = BuildUri(TagsPath);
        TagsResponse? tags;
        try
        {
            tags = await mHttp.GetFromJsonAsync<TagsResponse>(tagsUri, ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            throw;
        }
        catch(Exception ex)
        {
            throw new InvalidOperationException(
                $"Ollama is not reachable at {mSettings.Endpoint}. Install and run Ollama from https://ollama.com, then retry.",
                ex
            );
        }

        var availableNames = (tags?.Models ?? [])
                             .Select(m => m.Name)
                             .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!IsModelPresent(model, availableNames))
        {
            var pullUri = BuildUri(PullPath);
            var pullRequest = new PullRequest { Model = model };
            var pullResponse = await mHttp.PostAsJsonAsync(pullUri, pullRequest, ct);
            pullResponse.EnsureSuccessStatusCode();
        }
    }

    private static bool IsModelPresent(string model, IReadOnlySet<string> availableNames)
    {
        bool result = availableNames.Contains(model)
                      || availableNames.Contains($"{model}:latest")
                      || availableNames.Any(n => n.StartsWith(model, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private Uri BuildUri(string path) => new Uri(new Uri(mSettings.Endpoint), path);

    private const string TagsPath = "/api/tags";
    private const string PullPath = "/api/pull";

    #region JSON shapes

    private sealed class TagsResponse
    {
        [JsonPropertyName("models")]
        public List<ModelEntry> Models { get; set; } = [];

        internal sealed class ModelEntry
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;
        }
    }

    private sealed class PullRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;
    }

    #endregion
}
