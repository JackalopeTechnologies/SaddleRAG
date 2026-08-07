// DirectoryRegistrationRequest.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>Explicit user request to bind one local directory to a library.</summary>
public sealed record DirectoryRegistrationRequest(string LibraryId,
                                                  string RootPath,
                                                  bool Recursive,
                                                  IReadOnlyList<string> ExclusionPatterns,
                                                  IReadOnlyList<string>? AllowedExtensions = null,
                                                  string? Name = null,
                                                  string? Hint = null);
