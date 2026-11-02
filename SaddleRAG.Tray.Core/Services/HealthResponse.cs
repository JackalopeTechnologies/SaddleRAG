// HealthResponse.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Reads the <c>status</c> field of a <c>/health</c> response body. Docling answers
///     <c>{"status":"ok"}</c> when it can serve; the SaddleRAG service answers a JSON document, even
///     on HTTP 503.
/// </summary>
internal static class HealthResponse
{
    private const string StatusProperty = "status";
    private const string OkStatus = "ok";

    /// <summary>
    ///     Parses <paramref name="body" /> as JSON. Returns false when it is not JSON; otherwise
    ///     <paramref name="status" /> holds the <c>status</c> string, or null when the document has none.
    /// </summary>
    public static bool TryReadStatus(string body, out string? status)
    {
        ArgumentNullException.ThrowIfNull(body);

        var isJson = false;
        status = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            isJson = true;
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(StatusProperty, out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                status = value.GetString();
            }
        }
        catch(JsonException)
        {
            // Not JSON (an HTML error page, an empty body): the caller treats it as no health report.
            isJson = false;
        }

        return isJson;
    }

    /// <summary>True when <paramref name="body" /> is JSON whose <c>status</c> is "ok" (any case).</summary>
    public static bool ReportsOk(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return TryReadStatus(body, out string? status) && string.Equals(status, OkStatus, StringComparison.OrdinalIgnoreCase);
    }
}