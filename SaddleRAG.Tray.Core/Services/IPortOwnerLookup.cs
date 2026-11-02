// IPortOwnerLookup.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>Finds which process is listening on a local TCP port.</summary>
public interface IPortOwnerLookup
{
    /// <summary>
    ///     Returns the process listening on <paramref name="port" /> on any local address, or null
    ///     when nothing is listening there.
    /// </summary>
    /// <param name="port">The TCP port, 1 to 65535.</param>
    ListeningProcess? FindListeningProcess(int port);
}