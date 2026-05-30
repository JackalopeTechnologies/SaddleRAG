// McpServiceController.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.ServiceProcess;

#endregion

namespace SaddleRAG.Tray.Services;

public sealed class McpServiceController : IMcpServiceController
{
    private const string ServiceName = "SaddleRAGMcp";
    private static readonly TimeSpan smControlTimeout = TimeSpan.FromSeconds(30);

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

    public void Start()
    {
        ServiceController? sc = FindService();
        if (sc is not null)
        {
            using (sc)
            {
                if (sc.Status != ServiceControllerStatus.Running)
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, smControlTimeout);
                }
            }
        }
    }

    public void Stop()
    {
        ServiceController? sc = FindService();
        if (sc is not null)
        {
            using (sc)
            {
                if (sc.CanStop)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, smControlTimeout);
                }
            }
        }
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
