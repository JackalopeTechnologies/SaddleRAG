// ServiceActionOutcomeText.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>Plain, actionable wording for the balloon the tray shows when a service action ends.</summary>
public static class ServiceActionOutcomeText
{
    private const string StartedPast = "started";
    private const string StoppedPast = "stopped";
    private const string RestartedPast = "restarted";
    private const string StartVerb = "start";
    private const string StopVerb = "stop";
    private const string RestartVerb = "restart";

    private const string SucceededFormat = "{0} {1}.";
    private const string CancelledText = "Cancelled — Windows did not get administrator approval.";
    private const string NotAllowedFormat = "Refused: {0} is not a service the SaddleRAG tray may control.";
    private const string NotInstalledFormat = "Could not {0} {1}: it is not installed on this computer.";
    private const string TimedOutFormat =
        "Timed out waiting to {0} {1}. Check its status, and try again if it did not change.";
    // The script records a failed service call in the event log; a script that could not be run at
    // all is recorded only in the tray's own log, so both are named.
    private const string FailedFormat =
        "Could not {0} {1}. See the Windows Application event log (source SaddleRAG Service Actions) " +
        "and the tray log (%LOCALAPPDATA%\\SaddleRAG\\tray.log) for details.";
    private const string ScriptMissingText =
        "The SaddleRAG service helper script is missing from the install folder. Reinstall SaddleRAG.";
    private const string UnsafeInstallLocationRefusal =
        "Refused: the SaddleRAG install folder can be changed by non-administrators. ";
    private const string UnsafeInstallLocationAdvice =
        "Reinstall SaddleRAG to a protected folder such as C:\\Program Files.";
    private const string UnsafeInstallLocationText = UnsafeInstallLocationRefusal + UnsafeInstallLocationAdvice;

    /// <param name="outcome">How the action ended.</param>
    /// <param name="action">What was asked for.</param>
    /// <param name="subject">What it was asked of, as a phrase such as "SaddleRAG MCP service".</param>
    public static string Describe(ServiceActionOutcome outcome, ServiceAction action, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        string verb = Verb(action);
        string result = outcome switch
                        {
                            ServiceActionOutcome.Succeeded => string.Format(SucceededFormat, subject, PastTense(action)),
                            ServiceActionOutcome.Cancelled => CancelledText,
                            ServiceActionOutcome.NotAllowed => string.Format(NotAllowedFormat, subject),
                            ServiceActionOutcome.NotInstalled => string.Format(NotInstalledFormat, verb, subject),
                            ServiceActionOutcome.TimedOut => string.Format(TimedOutFormat, verb, subject),
                            ServiceActionOutcome.ScriptMissing => ScriptMissingText,
                            ServiceActionOutcome.UnsafeInstallLocation => UnsafeInstallLocationText,
                            _ => string.Format(FailedFormat, verb, subject)
                        };
        return result;
    }

    private static string PastTense(ServiceAction action) => action switch
                                                             {
                                                                 ServiceAction.Start => StartedPast,
                                                                 ServiceAction.Stop => StoppedPast,
                                                                 _ => RestartedPast
                                                             };

    private static string Verb(ServiceAction action) => action switch
                                                        {
                                                            ServiceAction.Start => StartVerb,
                                                            ServiceAction.Stop => StopVerb,
                                                            _ => RestartVerb
                                                        };
}
