// IDoclingLaunchRequestProbe.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>Asks the Monitor whether document work is waiting on a Docling that is not ready.</summary>
public interface IDoclingLaunchRequestProbe
{
    Task<bool> IsLaunchRequestedAsync(CancellationToken ct = default);
}
