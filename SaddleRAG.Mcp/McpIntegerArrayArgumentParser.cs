// McpIntegerArrayArgumentParser.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License in the repo root.

#region Usings

using System.Text.Json;

#endregion

namespace SaddleRAG.Mcp;

internal static class McpIntegerArrayArgumentParser
{
    internal static int[]? Parse(JsonElement? argument, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(parameterName);

        int[]? result = null;
        if (argument is { } value && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            try
            {
                result = value.ValueKind switch
                    {
                        JsonValueKind.Array => value.Deserialize<int[]>(),
                        JsonValueKind.String => JsonSerializer.Deserialize<int[]>(value.GetString() ?? string.Empty),
                        var _ => throw new ArgumentException($"'{parameterName}' must be a JSON array of integers or a JSON-encoded array string.",
                                                             parameterName
                                                            )
                    };
            }
            catch(JsonException exception)
            {
                throw new ArgumentException($"'{parameterName}' must be a JSON array of integers or a JSON-encoded array string.",
                                            parameterName,
                                            exception
                                           );
            }
        }

        return result;
    }
}