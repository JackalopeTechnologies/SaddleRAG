// IInstallLocationGuard.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Checks, before the tray elevates a script by path, that no ordinary user could have replaced
///     that script. A seam so the runner is testable on any platform.
/// </summary>
public interface IInstallLocationGuard
{
    InstallLocationVerdict Check(string scriptPath);
}
