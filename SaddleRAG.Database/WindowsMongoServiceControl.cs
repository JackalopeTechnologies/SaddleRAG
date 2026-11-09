// WindowsMongoServiceControl.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Runtime.Versioning;
using System.ServiceProcess;

namespace SaddleRAG.Database;

/// <summary>
///     <see cref="IMongoServiceControl" /> backed by the Windows Service Control
///     Manager.
/// </summary>
public sealed class WindowsMongoServiceControl : IMongoServiceControl
{
    public MongoServiceState GetState(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        MongoServiceState state = OperatingSystem.IsWindows()
                                      ? QueryState(serviceName)
                                      : MongoServiceState.Unsupported;
        return state;
    }

    public void Start(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Starting a Windows service requires Windows.");

        StartService(serviceName);
    }

    [SupportedOSPlatform("windows")]
    private static MongoServiceState QueryState(string serviceName)
    {
        using var controller = new ServiceController(serviceName);
        MongoServiceState state;
        try
        {
            state = Map(controller.Status);
        }
        catch (InvalidOperationException)
        {
            // ServiceController throws this when no service has the given name.
            state = MongoServiceState.NotInstalled;
        }

        return state;
    }

    [SupportedOSPlatform("windows")]
    private static void StartService(string serviceName)
    {
        using var controller = new ServiceController(serviceName);
        controller.Start();
    }

    [SupportedOSPlatform("windows")]
    private static MongoServiceState Map(ServiceControllerStatus status)
    {
        MongoServiceState state = status switch
        {
            ServiceControllerStatus.Stopped => MongoServiceState.Stopped,
            ServiceControllerStatus.StartPending => MongoServiceState.StartPending,
            ServiceControllerStatus.Running => MongoServiceState.Running,
            ServiceControllerStatus.StopPending => MongoServiceState.StopPending,
            _ => MongoServiceState.Other
        };
        return state;
    }
}
