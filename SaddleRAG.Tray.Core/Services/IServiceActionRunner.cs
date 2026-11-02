// IServiceActionRunner.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>Runs a start, stop or restart on named Windows services with administrator rights.</summary>
public interface IServiceActionRunner
{
    Task<ServiceActionOutcome> RunAsync(IReadOnlyList<string> serviceNames,
                                        ServiceAction action,
                                        CancellationToken ct);
}
