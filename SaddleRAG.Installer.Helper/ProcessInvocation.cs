// ProcessInvocation.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Installer.Helper;

/// <summary>Native executable, argument vector, and bounded runtime.</summary>
public sealed class ProcessInvocation
{
    public ProcessInvocation(string fileName,
                             IReadOnlyList<string> arguments,
                             TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be positive.");

        FileName = fileName;
        Arguments = arguments.ToArray();
        Timeout = timeout;
    }

    public string FileName { get; }

    public IReadOnlyList<string> Arguments { get; }

    public TimeSpan Timeout { get; }
}
