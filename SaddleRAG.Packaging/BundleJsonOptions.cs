// BundleJsonOptions.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace SaddleRAG.Packaging;

/// <summary>
///     Single source of truth for the JsonSerializerOptions used when
///     reading and writing manifest.json (and any other JSON file inside
///     a bundle). Exporter and importer both reference these options so
///     the wire format cannot drift between writer and reader.
/// </summary>
public static class BundleJsonOptions
{
    public static JsonSerializerOptions Default { get; } = BuildDefault();

    private static JsonSerializerOptions BuildDefault()
    {
        var options = new JsonSerializerOptions
                          {
                              WriteIndented = true,
                              DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                          };
        return options;
    }
}
