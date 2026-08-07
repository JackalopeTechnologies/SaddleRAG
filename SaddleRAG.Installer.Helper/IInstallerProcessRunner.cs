// IInstallerProcessRunner.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Installer.Helper;

/// <summary>Runs one native installer command without a shell intermediary.</summary>
public interface IInstallerProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(ProcessInvocation invocation,
                                          CancellationToken cancellationToken);
}
