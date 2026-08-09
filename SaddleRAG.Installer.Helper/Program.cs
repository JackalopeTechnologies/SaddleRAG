// Program.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Installer.Helper;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        StartAndMonitorOptions? options = StartAndMonitorOptions.Parse(args, out string parseError);
        int exitCode;
        if (options == null)
        {
            await Console.Error.WriteLineAsync(parseError);
            exitCode = InvalidArgumentsExitCode;
        }
        else
        {
            var processRunner = new InstallerProcessRunner();
            var serviceController = new ScWindowsServiceController(processRunner);
            using var httpClient = new HttpClient();
            var healthProbe = new HttpInstallerHealthProbe(httpClient);
            var command = new StartAndMonitorCommand(processRunner,
                                                     serviceController,
                                                     healthProbe);
            StartAndMonitorResult result = await command.RunAsync(options,
                                                                  CancellationToken.None);
            if (!string.IsNullOrEmpty(result.StandardOutput))
                await Console.Out.WriteLineAsync(result.StandardOutput);
            if (!string.IsNullOrEmpty(result.StandardError))
                await Console.Error.WriteLineAsync(result.StandardError);
            exitCode = result.Succeeded ? SuccessExitCode : StartupFailureExitCode;
        }

        return exitCode;
    }

    private const int SuccessExitCode = 0;
    private const int StartupFailureExitCode = 1;
    private const int InvalidArgumentsExitCode = 2;
}
