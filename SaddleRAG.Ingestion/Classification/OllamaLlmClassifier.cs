// OllamaLlmClassifier.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     LLM-based page classifier using Ollama chat completion.
///     Authoritative classification — overrides heuristic results.
///     All Ollama generate calls are routed through <see cref="IOllamaGenerateClient" />
///     so the generate path is unit-testable without a live Ollama instance.
/// </summary>
public class OllamaLlmClassifier : ILlmClassifier
{
    /// <summary>
    ///     Primary constructor: builds a real <see cref="OllamaApiClient" /> from
    ///     <paramref name="settings" /> and wraps it behind
    ///     <see cref="IOllamaGenerateClient" />. Used by the DI container.
    /// </summary>
    public OllamaLlmClassifier(IOptions<OllamaSettings> settings,
                               ILogger<OllamaLlmClassifier> logger)
    {
        mSettings = settings.Value;
        mLogger = logger;
        mGenerateClient = new OllamaApiClientAdapter(new OllamaApiClient(new Uri(mSettings.Endpoint)));
    }

    /// <summary>
    ///     Seam constructor: accepts an injected <see cref="IOllamaGenerateClient" />.
    ///     Used in tests and any future scenario that supplies an alternative adapter.
    /// </summary>
    internal OllamaLlmClassifier(IOptions<OllamaSettings> settings,
                                 IOllamaGenerateClient generateClient,
                                 ILogger<OllamaLlmClassifier> logger)
    {
        mSettings = settings.Value;
        mGenerateClient = generateClient;
        mLogger = logger;
    }

    private readonly IOllamaGenerateClient mGenerateClient;
    private readonly ILogger<OllamaLlmClassifier> mLogger;
    private readonly OllamaSettings mSettings;

    /// <summary>
    ///     Returns the current classifier version for this instance, used
    ///     by RescrubService to populate the manifest and decide whether
    ///     reclassification is needed on a future rescrub.
    /// </summary>
    public string GetCurrentVersion() => $"{mSettings.GetActiveClassificationModel().Name}-{PromptVersion}";

    /// <summary>
    ///     Classify a page using the LLM. Returns category and confidence.
    /// </summary>
    public async Task<(DocCategory Category, float Confidence)> ClassifyAsync(PageRecord page,
                                                                              string libraryHint,
                                                                              CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(libraryHint);

        var prompt = ClassificationPrompt.Build(page, libraryHint);

        var category = DocCategory.Unclassified;
        var confidence = 0f;

        try
        {
            var request = new GenerateRequest
                              {
                                  Model = mSettings.GetActiveClassificationModel().Name,
                                  Prompt = prompt,
                                  Stream = true
                              };

            var responseBuilder = new StringBuilder();
            await foreach(var token in mGenerateClient.GenerateAsync(request, ct))
            {
                if (responseBuilder.Length < MaxResponseChars)
                    responseBuilder.Append(token?.Response ?? string.Empty);
            }

            var parsed = ParseClassificationResponse(responseBuilder.ToString().Trim());
            category = parsed.Category;
            confidence = parsed.Confidence;

            mLogger.LogDebug("LLM classified {Url} as {Category} (confidence: {Confidence:F2})",
                             page.Url,
                             category,
                             confidence
                            );
        }
        catch(Exception ex)
        {
            mLogger.LogWarning(ex, "LLM classification failed for {Url}", page.Url);
        }

        return (category, confidence);
    }

    /// <summary>
    ///     Parse the LLM's raw text into a (Category, Confidence) tuple.
    ///     Strips ```json fences, attempts a strict JSON parse first, then
    ///     falls back to substring-matching a category name with a fixed
    ///     0.5 confidence. Exposed as <c>internal static</c> so tests can
    ///     lock in the parse contract without standing up an Ollama mock.
    /// </summary>
    internal static (DocCategory Category, float Confidence) ParseClassificationResponse(string responseText)
    {
        var cleaned = responseText
                      .Replace(JsonCodeFenceOpen, string.Empty)
                      .Replace(CodeFence, string.Empty)
                      .Trim();

        var category = DocCategory.Unclassified;
        var confidence = 0f;

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            if (root.TryGetProperty(CategoryKey, out var catProp))
            {
                var catString = catProp.GetString() ?? string.Empty;
                // Preserve the initial Unclassified when the model returns
                // a category name we don't recognize: Enum.TryParse writes
                // default(DocCategory) to the out parameter on failure,
                // which clobbers the initializer above.
                if (Enum.TryParse<DocCategory>(catString, ignoreCase: true, out var parsedCategory))
                    category = parsedCategory;
            }

            if (root.TryGetProperty(ConfidenceKey, out var confProp))
            {
                confidence = confProp.ValueKind switch
                    {
                        JsonValueKind.Number => (float) confProp.GetDouble(),
                        var _ => 0f
                    };
            }
        }
        catch(JsonException)
        {
            foreach(var cat in Enum.GetValues<DocCategory>())
            {
                if (cleaned.Contains(cat.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    category = cat;
                    confidence = 0.5f;
                    break;
                }
            }
        }

        return (category, confidence);
    }

    /// <summary>
    ///     Version string used by LibraryManifest.LastClassifierVersion to
    ///     detect when reclassification is needed during rescrub. Combines
    ///     the configured classification model with a manually-bumped prompt
    ///     version. Bump <see cref="PromptVersion" /> whenever the prompt
    ///     template changes meaningfully.
    /// </summary>
    public const string PromptVersion = "v1";

    private const int MaxResponseChars = 4096;
    private const string JsonCodeFenceOpen = "```json";
    private const string CodeFence = "```";
    private const string CategoryKey = "category";
    private const string ConfidenceKey = "confidence";
}
