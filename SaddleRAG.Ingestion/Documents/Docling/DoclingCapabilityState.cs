// DoclingCapabilityState.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>High-level state of the optional document-ingestion capability.</summary>
public enum DoclingCapabilityState
{
    NotChecked,
    Starting,
    Ready,
    Unavailable
}
