// IStructuredClassifierTextGenerator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json;

namespace SaddleRAG.Ingestion.Classification;

/// <summary>Optional generation support for enforcing the response schema during local sampling.</summary>
public interface IStructuredClassifierTextGenerator : IClassifierTextGenerator
{
    /// <summary>Generates a response constrained to the supplied JSON schema where supported.</summary>
    Task<string> GenerateAsync(string prompt, JsonElement responseSchema, CancellationToken ct = default);
}
