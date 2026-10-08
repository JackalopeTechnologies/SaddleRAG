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
    public static JsonElement Catalog(SubjectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        string schema = $$"""
            {"type":"object","properties":{"concepts":{"type":"array","minItems":1,"maxItems":{{SubjectClassificationLimits.MaxSecondarySubjects + 1}},
            "items":{"type":"object","properties":{
            "label":{{Text(SubjectClassificationLimits.MaxHeadingCharacters)}},
            "aliases":{"type":"array","maxItems":{{SubjectClassificationLimits.MaxHeadingCount}},"items":{{Text(SubjectClassificationLimits.MaxHeadingCharacters)}}},
            "confidence":{"type":"number","minimum":0,"maximum":1},"evidence":{{Evidence(descriptor)}}},
            "required":["label","aliases","confidence","evidence"],"additionalProperties":false} } },"required":["concepts"],"additionalProperties":false}
            """;
        return Parse(schema);
    }

    public static JsonElement Assignment(SubjectCatalogRecord catalog, SubjectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(descriptor);
        string identifiers = SubjectJson.Serialize(catalog.Concepts.Select(concept => concept.Id).ToArray());
        string selection = $$"""
            {"type":"object","properties":{"subjectId":{"type":"string","enum":{{identifiers}}},
            "confidence":{"type":"number","minimum":0,"maximum":1},"evidence":{{Evidence(descriptor)}}},
            "required":["subjectId","confidence","evidence"],"additionalProperties":false}
            """;
        string schema = $$"""
            {"type":"object","properties":{"primary":{{selection}},
            "secondary":{"type":"array","maxItems":{{SubjectClassificationLimits.MaxSecondarySubjects}},"items":{{selection}} } },
            "required":["primary","secondary"],"additionalProperties":false}
            """;
        return Parse(schema);
    }

    private static string Evidence(SubjectDescriptor descriptor)
    {
        string sourceIds = SubjectJson.Serialize(SubjectEvidence.Sources(descriptor).Keys.ToArray());
        return $$"""{"type":"array","minItems":1,"maxItems":{{SubjectClassificationLimits.MaxEvidenceCount}},"items":{"type":"string","enum":{{sourceIds}} } } """;
    }

    private static string Text(int maximumCharacters) =>
        $$"""{"type":"string","minLength":1,"maxLength":{{maximumCharacters}}} """;

    private static JsonElement Parse(string schema)
    {
        using JsonDocument document = JsonDocument.Parse(schema);
        return document.RootElement.Clone();
    }
}
