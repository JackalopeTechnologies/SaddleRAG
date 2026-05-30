// HfTreeSerializerContext.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Text.Json.Serialization;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

[JsonSerializable(typeof(List<HfTreeEntry>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class HfTreeSerializerContext : JsonSerializerContext
{
}
