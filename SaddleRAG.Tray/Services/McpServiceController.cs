// McpServiceController.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ServiceProcess;

#endregion

namespace SaddleRAG.Tray.Services;

public sealed class McpServiceController : IMcpServiceController
{
    private const string ServiceName = "SaddleRAGMcp";

    public McpServiceState GetState()
    {
        McpServiceState res = McpServiceState.NotInstalled;
        ServiceController? sc = FindService();
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

    private static ServiceController? FindService()
    {
        ServiceController[] all = ServiceController.GetServices();
        ServiceController? res = all.FirstOrDefault(s => string.Equals(s.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));
        foreach (ServiceController sc in all.Where(s => !ReferenceEquals(s, res)))
            sc.Dispose();
        return res;
    }
}
