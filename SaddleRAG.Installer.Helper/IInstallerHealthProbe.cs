// IInstallerHealthProbe.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Installer.Helper;

/// <summary>Reads the installed SaddleRAG service health endpoint.</summary>
public interface IInstallerHealthProbe
{
    Task<bool> IsHealthyAsync(Uri healthUrl,
                              TimeSpan requestTimeout,
                              CancellationToken cancellationToken);
}
