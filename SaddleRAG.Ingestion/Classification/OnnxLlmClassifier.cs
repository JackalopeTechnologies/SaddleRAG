// OnnxLlmClassifier.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     LLM-based page classifier backed by a local phi-3-mini-4k-instruct ONNX
///     GenAI model. Behaviorally equivalent to the Ollama
///     <see cref="OllamaLlmClassifier" />: it builds the same prompt via
///     <see cref="ClassificationPrompt" />, parses the model output with the
///     same <see cref="OllamaLlmClassifier.ParseClassificationResponse" /> contract,
///     and returns the same safe default (<see cref="DocCategory.Unclassified" />,
///     zero confidence) when generation fails. All
///     <c>Microsoft.ML.OnnxRuntimeGenAI</c> calls sit behind
///     <see cref="IClassifierGenerator" /> so the branching here is unit-testable
///     with a fake generator.
/// </summary>
public class OnnxLlmClassifier : ILlmClassifier
{
    public OnnxLlmClassifier(IClassifierGenerator generator,
                             ILogger<OnnxLlmClassifier> logger)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(logger);

        mGenerator = generator;
        mLogger = logger;
    }

    private readonly IClassifierGenerator mGenerator;
    private readonly ILogger<OnnxLlmClassifier> mLogger;

    /// <inheritdoc />
    public string BackendName => ClassifierBackendNames.Onnx;

    /// <inheritdoc />
    public string ModelId => mGenerator.ModelId;

    /// <inheritdoc />
    public string GetCurrentVersion() => $"{mGenerator.ModelId}-{ClassificationPrompt.PromptVersion}";

    /// <summary>
    ///     Classify a page with the local ONNX GenAI model. Returns category
    ///     and confidence. Never throws: a generation failure is logged and the
    ///     safe default (<see cref="DocCategory.Unclassified" />, 0 confidence)
    ///     is returned, matching the Ollama classifier's failure handling so
    ///     the pipeline treats the two backends identically.
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
            var responseText = await mGenerator.GenerateAsync(prompt, ct);

            var parsed = OllamaLlmClassifier.ParseClassificationResponse(responseText.Trim());
            category = parsed.Category;
            confidence = parsed.Confidence;

            mLogger.LogDebug("ONNX classified {Url} as {Category} (confidence: {Confidence:F2})",
                             page.Url,
                             category,
                             confidence
                            );
        }
        catch(Exception ex)
        {
            mLogger.LogWarning(ex, "ONNX classification failed for {Url}", page.Url);
        }

        return (category, confidence);
    }

}
