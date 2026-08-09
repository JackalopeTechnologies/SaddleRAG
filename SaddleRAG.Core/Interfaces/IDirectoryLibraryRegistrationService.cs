// IDirectoryLibraryRegistrationService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Core.Interfaces;

/// <summary>Persists a user-selected directory binding without starting a scan.</summary>
public interface IDirectoryLibraryRegistrationService
{
    Task<DirectoryRegistrationResult> RegisterAsync(DirectoryRegistrationRequest request,
                                                    string? profile,
                                                    CancellationToken ct = default);
}
