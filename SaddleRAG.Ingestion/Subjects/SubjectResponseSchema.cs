// SubjectResponseSchema.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json;
using SaddleRAG.Core.Models;

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>Generation-time bounds shared with subject response validation.</summary>
internal static class SubjectResponseSchema
{
    public static JsonElement Catalog()
    {
        string schema = $$"""
            {"type":"object","properties":{"concepts":{"type":"array","minItems":1,"maxItems":{{SubjectClassificationLimits.MaxSecondarySubjects + 1}},
            "items":{"type":"object","properties":{
            "label":{{Text(SubjectClassificationLimits.MaxHeadingCharacters)}},
            "aliases":{"type":"array","maxItems":{{SubjectClassificationLimits.MaxHeadingCount}},"items":{{Text(SubjectClassificationLimits.MaxHeadingCharacters)}}},
            "description":{{Text(SubjectClassificationLimits.MaxSummaryCharacters)}},"evidence":{{Evidence()}}},
            "required":["label","aliases","description","evidence"],"additionalProperties":false} } },"required":["concepts"],"additionalProperties":false}
            """;
        return Parse(schema);
    }

    public static JsonElement Assignment(SubjectCatalogRecord catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        string identifiers = SubjectJson.Serialize(catalog.Concepts.Select(concept => concept.Id).ToArray());
        string selection = $$"""
            {"type":"object","properties":{"subjectId":{"type":"string","enum":{{identifiers}}},
            "confidence":{"type":"number","minimum":0,"maximum":1},"evidence":{{Evidence()}}},
            "required":["subjectId","confidence","evidence"],"additionalProperties":false}
            """;
        string schema = $$"""
            {"type":"object","properties":{"primary":{{selection}},
            "secondary":{"type":"array","maxItems":{{SubjectClassificationLimits.MaxSecondarySubjects}},"items":{{selection}} } },
            "required":["primary","secondary"],"additionalProperties":false}
            """;
        return Parse(schema);
    }

    private static string Evidence() =>
        $$"""{"type":"array","minItems":1,"maxItems":{{SubjectClassificationLimits.MaxEvidenceCount}},"items":{{Text(SubjectClassificationLimits.MaxEvidenceCharacters)}}} """;

    private static string Text(int maximumCharacters) =>
        $$"""{"type":"string","minLength":1,"maxLength":{{maximumCharacters}}} """;

    private static JsonElement Parse(string schema)
    {
        using JsonDocument document = JsonDocument.Parse(schema);
        return document.RootElement.Clone();
    }
}
