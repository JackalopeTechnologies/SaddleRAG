// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Globalization;
using System.Text.Json;

namespace SaddleRAG.Ingestion.Subjects;

internal static class SubjectJson
{
    public static T Deserialize<T>(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                                                         EmptyJsonFormat,
                                                         value.Length));

        T? parsed = default;
        try
        {
            string cleaned = NormalizeFraming(value);
            using JsonDocument document = JsonDocument.Parse(cleaned);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException(RootObjectMessage);
            parsed = document.RootElement.Deserialize<T>(smOptions);
        }
        catch (JsonException ex)
        {
            long lineNumber = ex.LineNumber ?? UnknownJsonPosition;
            long bytePosition = ex.BytePositionInLine ?? UnknownJsonPosition;
            string message = string.Format(CultureInfo.InvariantCulture,
                                           InvalidJsonFormat,
                                           value.Length,
                                           lineNumber,
                                           bytePosition);
            throw new InvalidDataException(message);
        }

        if (parsed is null)
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                                                         EmptyJsonFormat,
                                                         value.Length));
        return parsed;
    }

    public static string Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, smOptions);
    }

    private static string NormalizeFraming(string value)
    {
        string result = value.Trim();
        result = result switch
        {
            var fenced when fenced.StartsWith(CodeFence, StringComparison.Ordinal) =>
                RemoveCodeFence(fenced),
            var wrapped when wrapped.StartsWith(JsonWrapperOpen, StringComparison.OrdinalIgnoreCase) =>
                RemoveJsonWrapper(wrapped),
            _ => result
        };

        return result.Trim();
    }

    private static string RemoveCodeFence(string value)
    {
        int firstLineEnd = value.IndexOf('\n');
        string opening = firstLineEnd >= 0 ? value[..firstLineEnd].Trim() : string.Empty;
        bool supportedOpening = string.Equals(opening, CodeFence, StringComparison.Ordinal) ||
                                string.Equals(opening, JsonCodeFence, StringComparison.OrdinalIgnoreCase);
        bool hasClosing = firstLineEnd >= 0 && value.EndsWith(CodeFence, StringComparison.Ordinal);
        if (!supportedOpening || !hasClosing)
            throw new JsonException(FramingMessage);

        int contentStart = firstLineEnd + 1;
        int contentLength = value.Length - contentStart - CodeFence.Length;
        string result = value.Substring(contentStart, contentLength);
        return result;
    }

    private static string RemoveJsonWrapper(string value)
    {
        if (!value.EndsWith(JsonWrapperClose, StringComparison.OrdinalIgnoreCase))
            throw new JsonException(FramingMessage);

        int contentLength = value.Length - JsonWrapperOpen.Length - JsonWrapperClose.Length;
        string result = value.Substring(JsonWrapperOpen.Length, contentLength);
        return result;
    }

    private const string CodeFence = "```";
    private const string JsonCodeFence = "```json";
    private const string JsonWrapperOpen = "<json>";
    private const string JsonWrapperClose = "</json>";
    private const string RootObjectMessage = "The response root must be a JSON object.";
    private const string FramingMessage = "The response has incomplete or unsupported JSON framing.";
    private const string InvalidJsonFormat =
        "The subject classifier returned invalid JSON (characters={0}, line={1}, byte={2}).";
    private const string EmptyJsonFormat =
        "The subject classifier returned an empty response (characters={0}).";
    private const long UnknownJsonPosition = -1;

    private static readonly JsonSerializerOptions smOptions = new()
                                                                  {
                                                                      PropertyNameCaseInsensitive = true,
                                                                      PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                                                                  };
}
