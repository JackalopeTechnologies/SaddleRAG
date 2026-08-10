// IDoclingLaunchRequestService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Monitor.Services;

/// <summary>Read-only view of whether document work is blocked on Docling not running.</summary>
public interface IDoclingLaunchRequestService
{
    Task<DoclingLaunchRequestStatus> GetAsync(string? profile, CancellationToken ct = default);
}
