// DoclingLaunchCoordinator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Ties the Monitor's read-only launch-request flag to the launcher.
///     <para>
///         The tray decides, not the service: a positive flag is information, and the
///         launcher's own single-shot guard bounds the result if the flag ever sticks.
///     </para>
/// </summary>
public sealed class DoclingLaunchCoordinator
{
    public DoclingLaunchCoordinator(IDoclingLaunchRequestProbe probe,
                                    IDoclingLauncher launcher,
                                    ILogger<DoclingLaunchCoordinator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(launcher);

        mProbe = probe;
        mLauncher = launcher;
        mLogger = logger;
    }

    private readonly IDoclingLaunchRequestProbe mProbe;
    private readonly IDoclingLauncher mLauncher;
    private readonly ILogger<DoclingLaunchCoordinator>? mLogger;

    /// <summary>
    ///     Runs one poll. Returns the launch outcome when one was attempted, or null when
    ///     nothing was wanted or the poll failed. Never throws: it is driven by a timer, and
    ///     a Monitor that is briefly unreachable is a recovered condition, not a failure.
    /// </summary>
    public async Task<DoclingLaunchOutcome?> PollOnceAsync(CancellationToken ct = default)
    {
        DoclingLaunchOutcome? result = null;
        try
        {
            if (await mProbe.IsLaunchRequestedAsync(ct))
            {
                result = await mLauncher.EnsureRunningAsync(ct);
                if (result == DoclingLaunchOutcome.NotRegistered)
                    mLogger?.LogWarning(NotRegisteredMessage);
            }
        }
        catch(Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException
                                     && !ct.IsCancellationRequested)
        {
            mLogger?.LogWarning(ex, PollFailedMessage);
        }

        return result;
    }

    private const string PollFailedMessage =
        "Could not read the Docling launch-request flag from the Monitor; will retry on the next poll";
    private const string NotRegisteredMessage =
        "Document work is waiting on Docling, but no Docling command is registered";
}
