// BundleJsonOptions.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace SaddleRAG.Packaging;

/// <summary>
///     Single source of truth for the JsonSerializerOptions used when
///     reading and writing all JSON and JSONL files inside a bundle.
///     Both manifest.json and JSONL files reference these options so
///     the wire format cannot drift between writer and reader.
/// </summary>
public static class BundleJsonOptions
{
    /// <summary>
    ///     Options for manifest.json: pretty-printed, null values omitted.
    /// </summary>
    public static JsonSerializerOptions Default { get; } = BuildDefault(writeIndented: true);

    /// <summary>
    ///     Options for JSONL files (pages.jsonl, chunks.jsonl): single-line
    ///     records, null values omitted. Shares all policies with <see cref="Default" />.
    /// </summary>
    public static JsonSerializerOptions JsonlDefault { get; } = BuildDefault(writeIndented: false);

    private static JsonSerializerOptions BuildDefault(bool writeIndented)
    {
        var options = new JsonSerializerOptions
                          {
                              WriteIndented = writeIndented,
                              DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                          };
        return options;
    }
}
