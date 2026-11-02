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
///     <para>
///         While the operator has deliberately stopped Docling (<see cref="DoclingOperatorIntent" />)
///         a waiting request does not start it: the poll starts nothing, and logs once per
///         suppression that it is holding off. The operator's next successful start lifts that.
///     </para>
/// </summary>
public sealed class DoclingLaunchCoordinator
{
    public DoclingLaunchCoordinator(IDoclingLaunchRequestProbe probe,
                                    IDoclingLauncher launcher,
                                    ILogger<DoclingLaunchCoordinator>? logger = null,
                                    DoclingOperatorIntent? intent = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(launcher);

        mProbe = probe;
        mLauncher = launcher;
        mLogger = logger;
        mIntent = intent;
    }

    private readonly DoclingOperatorIntent? mIntent;
    private int mLoggedSuppression;
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
                result = await LaunchUnlessSuppressedAsync(ct);
        }
        catch(Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException
                                     && !ct.IsCancellationRequested)
        {
            mLogger?.LogWarning(ex, PollFailedMessage);
        }

        return result;
    }

    private async Task<DoclingLaunchOutcome?> LaunchUnlessSuppressedAsync(CancellationToken ct)
    {
        DoclingLaunchOutcome? result = null;
        if (mIntent?.IsAutoStartSuppressed == true)
        {
            LogSuppressionOnce(mIntent);
        }
        else
        {
            result = await mLauncher.EnsureRunningAsync(ct);
            if (result == DoclingLaunchOutcome.NotRegistered)
                mLogger?.LogWarning(NotRegisteredMessage);
        }

        return result;
    }

    private void LogSuppressionOnce(DoclingOperatorIntent intent)
    {
        int suppression = intent.SuppressionCount;
        if (suppression != mLoggedSuppression)
        {
            mLoggedSuppression = suppression;
            mLogger?.LogInformation(SuppressedMessage);
        }
    }

    private const string SuppressedMessage =
        "Document work is waiting on Docling, but the operator stopped it; not starting it until the operator starts it again";
    private const string PollFailedMessage =
        "Could not read the Docling launch-request flag from the Monitor; will retry on the next poll";
    private const string NotRegisteredMessage =
        "Document work is waiting on Docling, but no Docling command is registered";
}
