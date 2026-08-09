// InstallerProcessRunner.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Diagnostics;

namespace SaddleRAG.Installer.Helper;

/// <summary>Runs native commands directly and drains both output pipes independently.</summary>
public sealed class InstallerProcessRunner : IInstallerProcessRunner
{
    public async Task<ProcessExecutionResult> RunAsync(ProcessInvocation invocation,
                                                       CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var startInfo = new ProcessStartInfo(invocation.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in invocation.Arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        ProcessExecutionResult result;
        if (!process.Start())
        {
            result = new ProcessExecutionResult(ProcessStartFailureExitCode,
                                                string.Empty,
                                                $"Failed to start '{invocation.FileName}'.");
        }
        else
        {
            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(invocation.Timeout);
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch(OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();
                timedOut = true;
            }

            string standardOutput = await standardOutputTask;
            string standardError = await standardErrorTask;
            if (timedOut)
            {
                string timeoutDetail = $"Command timed out after {invocation.Timeout.TotalSeconds:N0} seconds.";
                standardError = string.IsNullOrWhiteSpace(standardError)
                    ? timeoutDetail
                    : standardError.TrimEnd() + Environment.NewLine + timeoutDetail;
                result = new ProcessExecutionResult(ProcessTimeoutExitCode,
                                                    standardOutput,
                                                    standardError);
            }
            else
            {
                result = new ProcessExecutionResult(process.ExitCode,
                                                    standardOutput,
                                                    standardError);
            }
        }

        return result;
    }

    private const int ProcessStartFailureExitCode = -2;
    private const int ProcessTimeoutExitCode = -1;
}
