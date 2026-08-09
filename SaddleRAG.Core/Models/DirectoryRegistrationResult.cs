// DirectoryRegistrationResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>Sanitized result of an explicit directory registration.</summary>
public sealed record DirectoryRegistrationResult(string Status,
                                                 string LibraryId,
                                                 string? ReasonCode = null,
                                                 string? Detail = null);
