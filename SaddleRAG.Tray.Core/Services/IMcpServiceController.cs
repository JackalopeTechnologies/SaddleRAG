// IMcpServiceController.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Reads the MCP service's state. Starting and stopping are not here: an unelevated process
///     is denied them, so they go through <see cref="IServiceActionRunner" />.
/// </summary>
public interface IMcpServiceController
{
    McpServiceState GetState();
}
