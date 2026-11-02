// WindowsServiceStateReader.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ServiceProcess;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>Reads any Windows service's state from the service control manager.</summary>
public sealed class WindowsServiceStateReader : IServiceStateReader
{
    public McpServiceState GetState(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        McpServiceState res = McpServiceState.NotInstalled;
        ServiceController? sc = FindService(serviceName);
        if (sc is not null)
        {
            using (sc)
            {
                sc.Refresh();
                res = sc.Status switch
                      {
                          ServiceControllerStatus.Running => McpServiceState.Running,
                          ServiceControllerStatus.Stopped => McpServiceState.Stopped,
                          _ => McpServiceState.Transitioning
                      };
            }
        }
        return res;
    }

    private static ServiceController? FindService(string serviceName)
    {
        ServiceController[] all = ServiceController.GetServices();
        ServiceController? res = all.FirstOrDefault(s => string.Equals(s.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));
        foreach (ServiceController sc in all.Where(s => !ReferenceEquals(s, res)))
            sc.Dispose();
        return res;
    }
}