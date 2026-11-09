// IMongoServiceControl.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Database;

/// <summary>
///     Looks at, and starts, the Windows service that hosts a local MongoDB. It never
///     stops a service.
/// </summary>
public interface IMongoServiceControl
{
    /// <summary>
    ///     Reports the state of the named service. Returns
    ///     <see cref="MongoServiceState.NotInstalled" /> only when Windows says the
    ///     service does not exist; any other failure (including access denied)
    ///     propagates as <see cref="InvalidOperationException" />.
    /// </summary>
    MongoServiceState GetState(string serviceName);

    /// <summary>
    ///     Asks Windows to start the named service and returns without waiting for it
    ///     to finish starting. Lets <see cref="InvalidOperationException" /> (and its
    ///     inner <see cref="System.ComponentModel.Win32Exception" />) propagate.
    /// </summary>
    void Start(string serviceName);
}
