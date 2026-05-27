// BundleLibraryInfo.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.Json.Serialization;

#endregion

namespace SaddleRAG.Packaging;

/// <summary>
///     Library identity fields embedded in the bundle manifest.
/// </summary>
public sealed record BundleLibraryInfo
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("hint")]
    public required string Hint { get; init; }
}
