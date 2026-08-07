// ScWindowsServiceController.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Installer.Helper;

/// <summary>Uses the Windows service-control utility without a command shell.</summary>
public sealed class ScWindowsServiceController : IWindowsServiceController
{
    public ScWindowsServiceController(IInstallerProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        mProcessRunner = processRunner;
    }

    private readonly IInstallerProcessRunner mProcessRunner;

    public Task<ProcessExecutionResult> QueryAsync(string serviceName,
                                                   CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        return mProcessRunner.RunAsync(CreateInvocation([QueryCommand, serviceName]),
                                       cancellationToken);
    }

    public Task<ProcessExecutionResult> StartAsync(string serviceName,
                                                   CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        return mProcessRunner.RunAsync(CreateInvocation([StartCommand, serviceName]),
                                       cancellationToken);
    }

    private static ProcessInvocation CreateInvocation(IReadOnlyList<string> arguments) =>
        new(ServiceControlExecutable, arguments, smNativeCommandTimeout);

    private static readonly TimeSpan smNativeCommandTimeout = TimeSpan.FromSeconds(30);

    private const string ServiceControlExecutable = "sc.exe";
    private const string QueryCommand = "query";
    private const string StartCommand = "start";
}
