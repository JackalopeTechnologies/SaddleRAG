// ComponentKind.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>The four components the tray's Components window shows, in display order.</summary>
public enum ComponentKind
{
    /// <summary>The SaddleRAG MCP service.</summary>
    SaddleRagService,

    /// <summary>The MongoDB database SaddleRAG stores its index in.</summary>
    MongoDb,

    /// <summary>The Docling Serve document converter.</summary>
    Docling,

    /// <summary>The Ollama model server.</summary>
    Ollama
}
