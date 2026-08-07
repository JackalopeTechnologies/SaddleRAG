// DoclingEndpointDisplay.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

internal static class DoclingEndpointDisplay
{
    public static string Get(DoclingSettingsValidation validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        var result = validation.Endpoint?.GetLeftPart(UriPartial.Path).TrimEnd('/') ?? string.Empty;
        return result;
    }
}
