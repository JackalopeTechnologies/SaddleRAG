// WindowsMongoServiceControl.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.ComponentModel;
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
        catch (InvalidOperationException ex) when (IsServiceMissing(ex))
        {
            // Only "service does not exist" means not installed; any other failure,
            // such as access denied, propagates so it is not misdiagnosed.
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

    private static bool IsServiceMissing(InvalidOperationException ex)
    {
        bool missing = ex.InnerException is Win32Exception { NativeErrorCode: ErrorServiceDoesNotExist };
        return missing;
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

    // ERROR_SERVICE_DOES_NOT_EXIST
    private const int ErrorServiceDoesNotExist = 1060;
}
