// SkillFrontmatter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

#endregion

namespace SaddleRAG.ClientIntegration.Skills;

/// <summary>Parses embedded skill frontmatter without reducing it to prompt metadata.</summary>
internal static class SkillFrontmatter
{
    private const string Fence = "---";
    private const string NameKey = "name";
    private const string DescriptionKey = "description";
    private const int MaximumDepth = 32;

    internal static JsonObject Parse(string markdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        string yaml = Extract(markdown);
        JsonObject result;
        try
        {
            IDeserializer deserializer = new DeserializerBuilder()
                .WithDuplicateKeyChecking()
                .WithAttemptingUnquotedStringTypeDeserialization()
                .WithMaximumRecursion(MaximumDepth)
                .Build();
            object? document = deserializer.Deserialize<object>(yaml);
            ISerializer serializer = new SerializerBuilder()
                .JsonCompatible()
                .DisableAliases()
                .WithMaximumRecursion(MaximumDepth)
                .Build();
            result = JsonNode.Parse(serializer.Serialize(document)) as JsonObject
                     ?? throw new InvalidDataException("Skill frontmatter must be a mapping.");
        }
        catch (YamlException exception)
        {
            throw new InvalidDataException("Skill frontmatter is not valid YAML.", exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Skill frontmatter must contain JSON-compatible values.", exception);
        }

        ValidateRequiredString(result, NameKey);
        ValidateRequiredString(result, DescriptionKey);
        return result;
    }

    private static string Extract(string markdown)
    {
        using StringReader reader = new(markdown);
        if (!string.Equals(reader.ReadLine()?.Trim(), Fence, StringComparison.Ordinal))
            throw new InvalidDataException("Skill document must begin with YAML frontmatter.");
        List<string> lines = [];
        bool closed = false;
        string? line;
        while (!closed && (line = reader.ReadLine()) is not null)
        {
            closed = string.Equals(line.Trim(), Fence, StringComparison.Ordinal);
            if (!closed)
                lines.Add(line);
        }
        if (!closed)
            throw new InvalidDataException("Skill frontmatter has no closing fence.");
        return string.Join('\n', lines);
    }

    private static void ValidateRequiredString(JsonObject frontmatter, string key)
    {
        bool valid = frontmatter[key] is JsonValue value
                     && value.TryGetValue(out string? text)
                     && !string.IsNullOrWhiteSpace(text);
        if (!valid)
            throw new InvalidDataException($"Skill frontmatter requires a non-empty string '{key}'.");
    }
}
