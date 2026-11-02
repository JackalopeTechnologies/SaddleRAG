// IServiceStateReader.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Reads the state of a Windows service by name. The tray implements it over the service
///     control manager; tests substitute a fake.
/// </summary>
public interface IServiceStateReader
{
    /// <summary>
    ///     Returns the state of the named service: <see cref="McpServiceState.NotInstalled" /> when
    ///     no such service exists, <see cref="McpServiceState.Transitioning" /> while it is starting,
    ///     stopping, pausing or resuming.
    /// </summary>
    /// <param name="serviceName">The Windows service name, such as <c>SaddleRAGMcp</c>.</param>
    McpServiceState GetState(string serviceName);
}