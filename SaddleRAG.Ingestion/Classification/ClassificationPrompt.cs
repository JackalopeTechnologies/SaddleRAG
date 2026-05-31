// ClassificationPrompt.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     Shared prompt construction for the page classifiers. Both the Ollama
///     <see cref="OllamaLlmClassifier" /> and the ONNX <see cref="OnnxLlmClassifier" />
///     build the identical prompt here so the two backends classify pages by
///     the same instructions and category list. The matching output parser
///     lives on <see cref="OllamaLlmClassifier.ParseClassificationResponse" /> and is
///     reused by both backends as well; keeping the prompt and parser in one
///     place is what makes the two classifiers behaviorally interchangeable.
/// </summary>
internal static class ClassificationPrompt
{
    /// <summary>
    ///     Builds the classifier prompt for <paramref name="page" /> under the
    ///     library named by <paramref name="libraryHint" />. The content
    ///     preview is truncated to <see cref="MaxPreviewChars" /> characters so
    ///     the prompt stays within the model's context window. Behavior must
    ///     stay identical to the original inline Ollama prompt; the parser
    ///     depends on the model returning the JSON shape this prompt requests.
    /// </summary>
    internal static string Build(PageRecord page, string libraryHint)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(libraryHint);

        string contentPreview = page.RawContent.Length > MaxPreviewChars
                                    ? page.RawContent[..MaxPreviewChars]
                                    : page.RawContent;

        string jsonExample = """{"category": "...", "confidence": 0.0-1.0}""";

        string prompt = $"""
                         You are a documentation classifier. Given a page's metadata and content preview,
                         classify it into exactly one category. Respond with ONLY a JSON object:
                         {jsonExample}

                         Categories:
                         - Overview: Conceptual explanation, architecture, "about" pages
                         - HowTo: Step-by-step guide, tutorial, walkthrough
                         - Sample: Code samples, demos, example projects showing how to use the library
                         - Code: Library source code, implementation files (not usage examples)
                         - ApiReference: API docs — class, method, property, event reference
                         - ChangeLog: Release notes, migration guides, what's new
                         - Unclassified: Does not fit other categories

                         Library: {libraryHint}
                         URL: {page.Url}
                         Title: {page.Title}

                         Content preview:
                         {contentPreview}
                         """;

        return prompt;
    }

    /// <summary>
    ///     Version of the classification prompt template. Bump when the prompt
    ///     text changes so GetCurrentVersion() shifts and a rescrub
    ///     re-classifies. Shared by every classifier backend.
    /// </summary>
    public const string PromptVersion = "v1";

    /// <summary>
    ///     Maximum number of <see cref="PageRecord.RawContent" /> characters
    ///     included in the prompt's content preview. Longer content is
    ///     truncated to keep the prompt small and bounded.
    /// </summary>
    internal const int MaxPreviewChars = 500;
}
