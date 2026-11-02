// LocalListenerStopper.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Stops whatever is listening on Docling's or Ollama's port, as the signed-in user and only
///     after the operator agrees. Neither runs as a Windows service, so the tray finds the process
///     by its port; that process may not be the one expected, so the operator is always shown it
///     and told whether its name looks right.
///     <para>
///         An owner whose name looks right loses the processes it started too (Docling's Python
///         workers, Ollama's runners). Any other owner is stopped alone, so the tray never takes
///         down more of an unexpected program than the operator agreed to.
///     </para>
///     <para>
///         After the process exits, the port is watched for up to <see cref="PortReleaseWaitSeconds" />
///         seconds until it is free or a different process holds it. The second case is a respawn:
///         Ollama's desktop app starts <c>ollama serve</c> again by itself.
///     </para>
/// </summary>
internal sealed class LocalListenerStopper
{
    public LocalListenerStopper(IPortOwnerLookup ports,
                                IUserProcessStopper stopper,
                                IOperatorConfirmation confirmation,
                                TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(stopper);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(time);

        mPorts = ports;
        mStopper = stopper;
        mConfirmation = confirmation;
        mTime = time;
    }

    /// <summary>How long the port is watched after the process exits.</summary>
    public const int PortReleaseWaitSeconds = 15;

    /// <summary>How often the port is checked while it is watched.</summary>
    public const int PortPollMilliseconds = 250;

    /// <summary>How long the process is given to exit after it is killed.</summary>
    public const int ProcessExitSeconds = 10;

    private const string PythonProcessName = "python";
    private const string DoclingServeProcessName = "docling-serve";
    private const string OllamaProcessName = "ollama";
    private const string OllamaAppProcessName = "ollama app";

    // A shell (pwsh, powershell) is deliberately absent: it may have launched Docling, but it never
    // owns the listening socket, so a shell on the port is not what the operator expects.
    private static readonly string[] smDoclingProcessNames = [PythonProcessName, DoclingServeProcessName];
    private static readonly string[] smOllamaProcessNames = [OllamaProcessName, OllamaAppProcessName];

    private readonly IPortOwnerLookup mPorts;
    private readonly IUserProcessStopper mStopper;
    private readonly IOperatorConfirmation mConfirmation;
    private readonly TimeProvider mTime;

    /// <summary>Stops the process listening on <paramref name="port" />, once the operator agrees.</summary>
    /// <param name="component">The Docling or Ollama row being stopped.</param>
    /// <param name="port">The port it listens on.</param>
    /// <param name="ct">Cancels the waits.</param>
    public async Task<ComponentActionResult> StopAsync(ComponentDefinition component, int port, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(component);

        ComponentActionResult result;
        (ListeningProcess? owner, string? failure) = Read(port);
        if (failure is not null)
            result = Result(component, ComponentActionStatus.Failed, string.Format(UnreadableFormat, port, failure));
        else
            result = owner is null
                         ? Result(component, ComponentActionStatus.AlreadyInState, string.Format(NotRunningFormat, component.DisplayName))
                         : await ConfirmAndStopAsync(component, port, owner, ct);

        return result;
    }

    /// <summary>
    ///     True when the listener table could be read and shows nothing listening on
    ///     <paramref name="port" />. A table that cannot be read counts as not free.
    /// </summary>
    public bool IsPortFree(int port)
    {
        (ListeningProcess? owner, string? failure) = Read(port);
        bool result = failure is null && owner is null;
        return result;
    }

    /// <summary>True when the process name is one the component normally runs as.</summary>
    public static bool LooksExpected(ComponentKind kind, string processName)
    {
        ArgumentNullException.ThrowIfNull(processName);

        string bare = processName.EndsWith(ExeSuffix, StringComparison.OrdinalIgnoreCase)
                          ? processName[..^ExeSuffix.Length]
                          : processName;
        string[] expected = kind switch
                            {
                                ComponentKind.Docling => smDoclingProcessNames,
                                ComponentKind.Ollama => smOllamaProcessNames,
                                _ => []
                            };
        bool result = expected.Contains(bare, StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private async Task<ComponentActionResult> ConfirmAndStopAsync(ComponentDefinition component,
                                                               int port,
                                                               ListeningProcess owner,
                                                               CancellationToken ct)
    {
        bool expected = LooksExpected(component.Kind, owner.ProcessName);
        bool confirmed = await mConfirmation.ConfirmStopAsync(component, owner, expected);
        ComponentActionResult result = confirmed
                                        ? await StopOwnerAsync(component, port, owner, expected, ct)
                                        : Result(component,
                                               ComponentActionStatus.Cancelled,
                                               string.Format(CancelledFormat, component.DisplayName));
        return result;
    }

    private async Task<ComponentActionResult> StopOwnerAsync(ComponentDefinition component,
                                                          int port,
                                                          ListeningProcess owner,
                                                          bool expected,
                                                          CancellationToken ct)
    {
        ProcessStopOutcome outcome = await mStopper.StopAsync(owner,
                                                              entireProcessTree: expected,
                                                              TimeSpan.FromSeconds(ProcessExitSeconds),
                                                              ct);
        ComponentActionResult result = outcome switch
                                    {
                                        ProcessStopOutcome.Stopped => await WaitForPortAsync(component, port, owner, ct),
                                        ProcessStopOutcome.NotFound => AfterVanished(component, port),
                                        ProcessStopOutcome.AccessDenied =>
                                            Result(component,
                                                 ComponentActionStatus.Denied,
                                                 string.Format(DeniedFormat, owner.ProcessName, owner.ProcessId)),
                                        _ => Result(component,
                                                  ComponentActionStatus.TimedOut,
                                                  string.Format(ExitTimedOutFormat, owner.ProcessName, owner.ProcessId, ProcessExitSeconds))
                                    };
        return result;
    }

    private async Task<ComponentActionResult> WaitForPortAsync(ComponentDefinition component,
                                                            int port,
                                                            ListeningProcess stopped,
                                                            CancellationToken ct)
    {
        DateTimeOffset deadline = mTime.GetUtcNow() + TimeSpan.FromSeconds(PortReleaseWaitSeconds);
        (ListeningProcess? now, string? failure) = Read(port);
        while (!IsSettled(now, failure, stopped) && mTime.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(PortPollMilliseconds), mTime, ct);
            (now, failure) = Read(port);
        }

        ComponentActionResult result;
        if (IsSettled(now, failure, stopped))
            result = now is null
                         ? Result(component, ComponentActionStatus.Succeeded, string.Format(StoppedFormat, component.DisplayName))
                         : Result(component,
                                ComponentActionStatus.Succeeded,
                                string.Format(RespawnedFormat, component.DisplayName, now.ProcessName, now.ProcessId, port));
        else
            result = Result(component,
                          ComponentActionStatus.TimedOut,
                          string.Format(PortHeldFormat, component.DisplayName, port, PortReleaseWaitSeconds));

        return result;
    }

    // A read failure while watching says nothing about the port, so it is not settled; the watch
    // goes on, and a table that stays unreadable ends as a timeout.
    private static bool IsSettled(ListeningProcess? now, string? failure, ListeningProcess stopped) =>
        failure is null && (now is null || now.ProcessId != stopped.ProcessId);

    private ComponentActionResult AfterVanished(ComponentDefinition component, int port)
    {
        // The process exited, or its id now belongs to a different process, before it could be
        // stopped. A free port means the component is already down. Anything still listening was
        // never shown to the operator, so it is left alone and the operator can look again.
        (ListeningProcess? now, string? failure) = Read(port);
        ComponentActionResult result = failure is null && now is null
                                        ? Result(component,
                                               ComponentActionStatus.AlreadyInState,
                                               string.Format(AlreadyStoppedFormat, component.DisplayName))
                                        : Result(component,
                                               ComponentActionStatus.Failed,
                                               string.Format(ChangedFormat, port, component.DisplayName));
        return result;
    }

    private (ListeningProcess? Owner, string? Failure) Read(int port)
    {
        (ListeningProcess? Owner, string? Failure) result;
        try
        {
            result = (mPorts.FindListeningProcess(port), null);
        }
        catch(Win32Exception ex)
        {
            result = (null, ex.Message);
        }

        return result;
    }

    private static ComponentActionResult Result(ComponentDefinition component, ComponentActionStatus status, string message) =>
        new(component.Kind, status, message);

    private const string ExeSuffix = ".exe";
    private const string UnreadableFormat = "Could not read which process is listening on port {0}: {1}";
    private const string NotRunningFormat = "{0} is not running.";
    private const string AlreadyStoppedFormat = "{0} had already stopped.";
    private const string CancelledFormat = "Stopping {0} was cancelled; nothing was stopped.";
    private const string StoppedFormat = "{0} stopped.";
    private const string RespawnedFormat = "{0} stopped, and {1} (process {2}) started listening on port {3} in its place.";
    private const string DeniedFormat =
        "Windows did not let the tray stop {0} (process {1}): it runs under another account or as administrator. Stop it from that account.";
    private const string ExitTimedOutFormat = "{0} (process {1}) did not exit within {2} seconds.";
    private const string PortHeldFormat = "{0} was stopped, but port {1} was still in use after {2} seconds.";
    private const string ChangedFormat =
        "The process on port {0} changed before it could be stopped, so nothing was stopped. Check {1}'s status and try again.";
}
