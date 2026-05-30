// HfTreeEntry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Text.Json.Serialization;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Represents one entry returned by the HuggingFace model-tree API
///     (<c>GET /api/models/{repoId}/tree/main/{folder}</c>).
///     Only <see cref="Type" /> and <see cref="Path" /> are needed for
///     folder download; <see cref="Size" /> is captured for logging.
/// </summary>
internal sealed class HfTreeEntry
{
    /// <summary>Entry type: <c>"file"</c> or <c>"directory"</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>
    ///     Repo-relative path of this entry, e.g.
    ///     <c>"cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/model.onnx"</c>.
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    /// <summary>File size in bytes (0 for directories).</summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }
}
