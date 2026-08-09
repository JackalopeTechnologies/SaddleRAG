// IClassifierTextGenerator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Classification;

/// <summary>Backend-neutral raw text generation used by structured classifiers.</summary>
public interface IClassifierTextGenerator
{
    string BackendName { get; }

    string ModelId { get; }

    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);
}
