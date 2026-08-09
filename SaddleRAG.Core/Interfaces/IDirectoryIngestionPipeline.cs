// IDirectoryIngestionPipeline.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Core.Interfaces;

/// <summary>Processes a complete directory candidate without publishing its version pointer.</summary>
public interface IDirectoryIngestionPipeline
{
    Task<DirectoryIngestionPipelineResult> ExecuteAsync(DirectoryIngestionRequest request,
                                                        Action<DirectoryScanProgress>? onProgress,
                                                        CancellationToken ct = default);
}
