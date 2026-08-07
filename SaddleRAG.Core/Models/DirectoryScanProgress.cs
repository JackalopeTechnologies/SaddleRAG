// DirectoryScanProgress.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>Sanitized progress for one explicitly requested directory scan.</summary>
public sealed record DirectoryScanProgress(int FilesDiscovered,
                                           int SupportedDocuments,
                                           int DocumentsCompleted,
                                           string? CurrentRelativePath);
