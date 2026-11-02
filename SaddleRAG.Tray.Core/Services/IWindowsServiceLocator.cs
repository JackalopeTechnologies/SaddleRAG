// IWindowsServiceLocator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Finds the Windows service that runs a given program. Behind a seam so the component catalog
///     can be tested without depending on what is installed on the build machine.
/// </summary>
public interface IWindowsServiceLocator
{
    /// <summary>
    ///     The name of the service whose executable has the file name <paramref name="executableFileName" />
    ///     (for example <c>mongod.exe</c>), or null when no service runs it. The real implementation
    ///     reads each service's <c>ImagePath</c> under <c>HKLM\SYSTEM\CurrentControlSet\Services</c>,
    ///     which ordinary users can read, and identifies the executable by the same rule the
    ///     elevated restart script uses (see <see cref="ServiceImagePath" />).
    /// </summary>
    string? FindServiceByExecutable(string executableFileName);
}
