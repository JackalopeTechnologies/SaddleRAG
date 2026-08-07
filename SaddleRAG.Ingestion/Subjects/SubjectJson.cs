// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Encodings.Web;
using System.Text.Json;

namespace SaddleRAG.Ingestion.Subjects;

internal static class SubjectJson
{
    public static T Deserialize<T>(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        string cleaned = RemoveCodeFence(value);
        T? parsed = default;
        try
        {
            parsed = JsonSerializer.Deserialize<T>(cleaned, smOptions);
        }
        catch(JsonException ex)
        {
            throw new InvalidDataException("The subject classifier returned invalid JSON.", ex);
        }

        if (parsed is null)
            throw new InvalidDataException("The subject classifier returned an empty JSON value.");
        return parsed;
    }

    public static string Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, smOptions);
    }

    private static string RemoveCodeFence(string value)
    {
        string result = value.Trim();
        if (result.StartsWith(CodeFence, StringComparison.Ordinal))
        {
            int firstLineEnd = result.IndexOf('\n');
            if (firstLineEnd >= 0)
                result = result[(firstLineEnd + 1)..];
            if (result.EndsWith(CodeFence, StringComparison.Ordinal))
                result = result[..^CodeFence.Length];
        }

        return result.Trim();
    }

    private const string CodeFence = "```";

    private static readonly JsonSerializerOptions smOptions = new()
                                                                  {
                                                                      Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                                                                      PropertyNameCaseInsensitive = true,
                                                                      PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                                                                  };
}
