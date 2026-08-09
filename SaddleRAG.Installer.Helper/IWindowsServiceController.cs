// IWindowsServiceController.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Installer.Helper;

/// <summary>Queries and starts the one Windows service owned by this installer.</summary>
public interface IWindowsServiceController
{
    Task<ProcessExecutionResult> QueryAsync(string serviceName,
                                            CancellationToken cancellationToken);

    Task<ProcessExecutionResult> StartAsync(string serviceName,
                                            CancellationToken cancellationToken);
}
