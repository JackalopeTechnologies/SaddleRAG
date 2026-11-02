// ComponentController.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Starts, stops and restarts the components the Components window shows, one operator action
///     at a time. It never acts on its own, and never acts on a component hosted on another computer.
///     <list type="bullet">
///         <item>
///             The SaddleRAG service and MongoDB are Windows services, changed through the elevated
///             service runner (one Windows administrator prompt per action).
///         </item>
///         <item>
///             Docling and Ollama run in the signed-in user's session and are never started or stopped
///             elevated. Stopping one always asks the operator first (<see cref="LocalListenerStopper" />);
///             starting Docling goes through the registered launcher, and Ollama through
///             <see cref="OllamaServeLauncher" />.
///         </item>
///         <item>
///             The Docling launcher starts Docling at most once per tray session. An operator start or
///             restart lifts that only when nothing listens on Docling's port and the process the
///             launcher started has exited (stopped, or crashed). A process on the port, or a launched
///             process still loading, keeps it in place, so a second server is never started beside one.
///         </item>
///         <item>
///             An operator stop of Docling, or a restart whose start step fails after its stop worked,
///             keeps the launch-request coordinator from starting it again until the operator next
///             starts or restarts it successfully (<see cref="DoclingOperatorIntent" />).
///         </item>
///         <item>
///             A restart never stops what it cannot start again. Before asking the operator or stopping
///             anything it checks that Docling is registered and that Ollama has an executable path; if
///             not, nothing is touched. A plain stop is not affected.
///         </item>
///     </list>
///     <para>
///         Restart all goes Docling, then Ollama, then one elevated restart of MongoDB and the SaddleRAG
///         service together: SaddleRAG comes back last, after everything it depends on, and the
///         operator sees a single administrator prompt. It stops at the first action that fails, is
///         denied, times out or is cancelled. A Docling or Ollama that cannot be started again is
///         reported as not controllable and left alone, so an unused component never blocks the restart
///         of MongoDB and SaddleRAG. A single restart of that row reports it as failed.
///     </para>
///     <para>
///         Success, an operator cancellation, a component already in the requested state, and a
///         refused busy request log at Information; a failed, denied or timed-out action, or a local
///         component the tray cannot control, logs at Warning. Error is reserved for an exception that
///         escapes an action; it is logged and rethrown.
///     </para>
/// </summary>
public sealed class ComponentController
{
    public ComponentController(IServiceActionRunner services,
                               ComponentStatusProbe probe,
                               IPortOwnerLookup ports,
                               IUserProcessStopper stopper,
                               IDoclingLauncher docling,
                               IProcessStarter ollamaStarter,
                               IOperatorConfirmation confirmation,
                               ILogger<ComponentController>? logger = null,
                               TimeProvider? time = null,
                               DoclingOperatorIntent? doclingIntent = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(stopper);
        ArgumentNullException.ThrowIfNull(docling);
        ArgumentNullException.ThrowIfNull(ollamaStarter);
        ArgumentNullException.ThrowIfNull(confirmation);

        TimeProvider clock = time ?? TimeProvider.System;
        mServices = services;
        mDocling = docling;
        mListeners = new LocalListenerStopper(ports, stopper, confirmation, clock);
        mOllama = new OllamaServeLauncher(probe, ollamaStarter, clock);
        mLogger = logger;
        mDoclingIntent = doclingIntent;
    }

    private readonly DoclingOperatorIntent? mDoclingIntent;
    private readonly IServiceActionRunner mServices;
    private readonly IDoclingLauncher mDocling;
    private readonly LocalListenerStopper mListeners;
    private readonly OllamaServeLauncher mOllama;
    private readonly ILogger<ComponentController>? mLogger;

    // One action at a time. Only WaitAsync and Release are used, never AvailableWaitHandle, so the
    // semaphore holds no kernel handle and needs no disposal.
    private readonly SemaphoreSlim mGate = new(initialCount: 1, maxCount: 1);

    /// <summary>
    ///     Where <c>ollama serve</c>'s own output is captured when the tray starts it. The tray gives
    ///     the controller an <see cref="IProcessStarter" /> that writes here.
    /// </summary>
    public static string OllamaLaunchLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     LogDirectoryName,
                     LogsFolderName,
                     OllamaLogFileName);

    /// <summary>Starts one component, or reports why it cannot.</summary>
    public Task<ComponentActionResult> StartAsync(ComponentDefinition component, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(component);
        return RunExclusiveAsync(component, ServiceAction.Start, ct);
    }

    /// <summary>Stops one component, asking the operator first when it is Docling or Ollama.</summary>
    public Task<ComponentActionResult> StopAsync(ComponentDefinition component, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(component);
        return RunExclusiveAsync(component, ServiceAction.Stop, ct);
    }

    /// <summary>Restarts one component, asking the operator first when it is Docling or Ollama.</summary>
    public Task<ComponentActionResult> RestartAsync(ComponentDefinition component, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(component);
        return RunExclusiveAsync(component, ServiceAction.Restart, ct);
    }

    /// <summary>
    ///     Restarts every local component in dependency order: Docling, Ollama, then MongoDB and the
    ///     SaddleRAG service in one elevated call. Rows on another computer are reported as not
    ///     controllable and do not stop the sequence. At the first failure the sequence stops; the rows
    ///     not reached are left out of the results and named in the last result's message.
    /// </summary>
    /// <param name="components">The rows the window shows, in any order.</param>
    /// <param name="ct">Cancels the sequence.</param>
    public async Task<IReadOnlyList<ComponentActionResult>> RestartAllAsync(IReadOnlyList<ComponentDefinition> components,
                                                                            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(components);

        IReadOnlyList<ComponentActionResult> result;
        if (await mGate.WaitAsync(millisecondsTimeout: 0, ct))
        {
            try
            {
                result = await RestartSequenceAsync(components, ct);
            }
            finally
            {
                mGate.Release();
            }
        }
        else
        {
            ComponentKind kind = components.Count > 0 ? components[0].Kind : ComponentKind.SaddleRagService;
            ComponentActionResult busy = new(kind, ComponentActionStatus.Busy, BusyText);
            Log(AllComponentsName, RestartAllVerb, busy, hostedLocally: true);
            result = [busy];
        }

        return result;
    }

    private async Task<ComponentActionResult> RunExclusiveAsync(ComponentDefinition component,
                                                                ServiceAction action,
                                                                CancellationToken ct)
    {
        ComponentActionResult result;
        if (await mGate.WaitAsync(millisecondsTimeout: 0, ct))
        {
            try
            {
                result = await ExecuteLoggingEscapesAsync(component, action, partOfRestartAll: false, ct);
            }
            finally
            {
                mGate.Release();
            }
        }
        else
        {
            result = new ComponentActionResult(component.Kind, ComponentActionStatus.Busy, BusyText);
        }

        Log(component.DisplayName, Verb(action), result, component.HostedLocally);
        return result;
    }

    private async Task<ComponentActionResult> ExecuteLoggingEscapesAsync(ComponentDefinition component,
                                                                         ServiceAction action,
                                                                         bool partOfRestartAll,
                                                                         CancellationToken ct)
    {
        ComponentActionResult result;
        try
        {
            result = await ExecuteAsync(component, action, partOfRestartAll, ct);
        }
        catch(Exception ex) when (ex is not OperationCanceledException)
        {
            mLogger?.LogError(ex, UnexpectedFailureMessage, component.DisplayName, Verb(action));
            throw;
        }

        return result;
    }

    private async Task<ComponentActionResult> ExecuteAsync(ComponentDefinition component,
                                                           ServiceAction action,
                                                           bool partOfRestartAll,
                                                           CancellationToken ct)
    {
        ComponentActionResult result;
        if (component.HostedLocally)
            result = component.Kind switch
                     {
                         ComponentKind.Docling or ComponentKind.Ollama =>
                             await RunListenerAsync(component, action, partOfRestartAll, ct),
                         _ => await RunServiceAsync(component, action, ct)
                     };
        else
            result = NotControllable(component, RemoteFormat);

        return result;
    }

    private async Task<ComponentActionResult> RunServiceAsync(ComponentDefinition component,
                                                              ServiceAction action,
                                                              CancellationToken ct)
    {
        ComponentActionResult result = component.WindowsServiceName is { } serviceName
                                           ? FromServiceOutcome(component.Kind,
                                                                await mServices.RunAsync([serviceName], action, ct),
                                                                action,
                                                                component.DisplayName)
                                           : NotControllable(component, NoServiceFormat);
        return result;
    }

    private async Task<ComponentActionResult> RunListenerAsync(ComponentDefinition component,
                                                               ServiceAction action,
                                                               bool partOfRestartAll,
                                                               CancellationToken ct)
    {
        ComponentActionResult result;
        if (component.Endpoint is { } endpoint)
            result = action switch
                     {
                         ServiceAction.Start => await StartListenerAsync(component, endpoint.Port, ct),
                         ServiceAction.Stop => await StopListenerAsync(component, endpoint.Port, ct),
                         _ => await RestartListenerAsync(component, endpoint.Port, partOfRestartAll, ct)
                     };
        else
            result = NotControllable(component, NoAddressFormat);

        return result;
    }

    private async Task<ComponentActionResult> RestartListenerAsync(ComponentDefinition component,
                                                                   int port,
                                                                   bool partOfRestartAll,
                                                                   CancellationToken ct)
    {
        // Never stop what cannot be started again: the check comes before the operator is asked
        // and before anything is stopped. Restart all reports such a row as not controllable and
        // moves on; a single restart reports it as failed.
        ComponentActionResult result;
        if (CannotStartReason(component) is { } reason)
        {
            ComponentActionStatus status = partOfRestartAll
                                               ? ComponentActionStatus.NotControllable
                                               : ComponentActionStatus.Failed;
            result = new ComponentActionResult(component.Kind,
                                               status,
                                               string.Format(NotRestartableFormat, component.DisplayName, reason));
        }
        else
        {
            ComponentActionResult stop = await StopListenerAsync(component, port, ct);
            bool stopped = stop.Status is ComponentActionStatus.Succeeded or ComponentActionStatus.AlreadyInState;
            result = stopped ? await StartListenerAsync(component, port, ct) : stop;
        }

        return result;
    }

    // The operator's stop of Docling keeps the launch-request coordinator from starting it again
    // (see DoclingOperatorIntent). It takes effect as soon as the stop has worked, so a restart whose
    // start then fails leaves Docling stopped and suppressed, and the coordinator cannot start it in
    // the gap between the two steps.
    private async Task<ComponentActionResult> StopListenerAsync(ComponentDefinition component, int port, CancellationToken ct)
    {
        ComponentActionResult result = await mListeners.StopAsync(component, port, ct);
        if (component.Kind == ComponentKind.Docling &&
            result.Status is ComponentActionStatus.Succeeded or ComponentActionStatus.AlreadyInState)
            mDoclingIntent?.SuppressAutoStart();

        return result;
    }

    private string? CannotStartReason(ComponentDefinition component) => component.Kind switch
                                                                        {
                                                                            ComponentKind.Docling when !mDocling.IsRegistered =>
                                                                                DoclingNotRegisteredReason,
                                                                            ComponentKind.Ollama when string.IsNullOrWhiteSpace(component.ExecutablePath) =>
                                                                                OllamaNoExecutableReason,
                                                                            _ => null
                                                                        };

    private Task<ComponentActionResult> StartListenerAsync(ComponentDefinition component, int port, CancellationToken ct) =>
        component.Kind == ComponentKind.Docling
            ? StartDoclingAsync(component, port, ct)
            : mOllama.StartAsync(component, ct);

    private async Task<ComponentActionResult> StartDoclingAsync(ComponentDefinition component,
                                                                int port,
                                                                CancellationToken ct)
    {
        // The launcher starts Docling at most once per tray session. It lifts that only when nothing
        // listens on the port and the process it started has exited (stopped, or crashed), checking
        // both under the same lock as a launch. Whether it did is not the operator's concern: the
        // launch outcome below says what happened.
        _ = mDocling.TryAllowRelaunch(() => mListeners.IsPortFree(port));

        DoclingLaunchOutcome outcome = await mDocling.EnsureRunningAsync(ct);
        string name = component.DisplayName;
        ComponentActionResult result = outcome switch
                                       {
                                           DoclingLaunchOutcome.Ready =>
                                               new(component.Kind, ComponentActionStatus.Succeeded, string.Format(StartedFormat, name)),
                                           DoclingLaunchOutcome.AlreadyRunning =>
                                               new(component.Kind, ComponentActionStatus.Succeeded, string.Format(AlreadyRunningFormat, name)),
                                           DoclingLaunchOutcome.NotRegistered =>
                                               new(component.Kind, ComponentActionStatus.Failed, DoclingNotRegisteredText),
                                           DoclingLaunchOutcome.Timeout =>
                                               new(component.Kind,
                                                   ComponentActionStatus.TimedOut,
                                                   string.Format(DoclingTimeoutFormat, name, ProcessStarter.DefaultLogPath)),
                                           _ => new(component.Kind,
                                                    ComponentActionStatus.Failed,
                                                    string.Format(DoclingFailedFormat, name, ProcessStarter.DefaultLogPath))
                                       };
        if (result.Status == ComponentActionStatus.Succeeded)
            mDoclingIntent?.LiftAutoStartSuppression();

        return result;
    }

    private async Task<IReadOnlyList<ComponentActionResult>> RestartSequenceAsync(IReadOnlyList<ComponentDefinition> components,
                                                                                 CancellationToken ct)
    {
        List<ComponentDefinition> listeners = [.. OfKind(components, ComponentKind.Docling), .. OfKind(components, ComponentKind.Ollama)];
        List<ComponentDefinition> services = [.. OfKind(components, ComponentKind.MongoDb), .. OfKind(components, ComponentKind.SaddleRagService)];
        var done = new List<(ComponentDefinition Row, ComponentActionResult Result)>();
        var halted = false;
        for (var index = 0; index < listeners.Count && !halted; index++)
        {
            ComponentDefinition row = listeners[index];
            ComponentActionResult outcome =
                await ExecuteLoggingEscapesAsync(row, ServiceAction.Restart, partOfRestartAll: true, ct);
            halted = Halts(outcome.Status);
            if (halted)
                outcome = outcome with { Message = WithNotRestarted(outcome.Message, [.. listeners.Skip(index + 1), .. services]) };

            done.Add((row, outcome));
        }

        if (!halted)
            done.AddRange(await RestartServicesTogetherAsync(services, ct));

        foreach ((ComponentDefinition row, ComponentActionResult outcome) in done)
            Log(row.DisplayName, RestartAllVerb, outcome, row.HostedLocally);

        IReadOnlyList<ComponentActionResult> result = [.. done.Select(entry => entry.Result)];
        return result;
    }

    private async Task<List<(ComponentDefinition Row, ComponentActionResult Result)>> RestartServicesTogetherAsync(
        List<ComponentDefinition> services,
        CancellationToken ct)
    {
        var included = new List<(ComponentDefinition Row, string ServiceName)>();
        foreach (ComponentDefinition row in services.Where(row => row.HostedLocally))
        {
            if (row.WindowsServiceName is { } serviceName)
                included.Add((row, serviceName));
        }

        string subject = string.Join(SubjectSeparator, included.Select(entry => entry.Row.DisplayName));

        // Only read when at least one service is included; the initial value is never reported.
        ServiceActionOutcome outcome = ServiceActionOutcome.Succeeded;
        if (included.Count > 0)
        {
            try
            {
                outcome = await mServices.RunAsync([.. included.Select(entry => entry.ServiceName)], ServiceAction.Restart, ct);
            }
            catch(Exception ex) when (ex is not OperationCanceledException)
            {
                mLogger?.LogError(ex, UnexpectedFailureMessage, subject, RestartAllVerb);
                throw;
            }
        }

        List<(ComponentDefinition Row, ComponentActionResult Result)> result =
        [
            .. services.Select(row => (row, row switch
                                            {
                                                { HostedLocally: false } => NotControllable(row, RemoteFormat),
                                                { WindowsServiceName: null } => NotControllable(row, NoServiceFormat),
                                                _ => FromServiceOutcome(row.Kind, outcome, ServiceAction.Restart, subject)
                                            }))
        ];
        return result;
    }

    private static IEnumerable<ComponentDefinition> OfKind(IReadOnlyList<ComponentDefinition> components, ComponentKind kind) =>
        components.Where(component => component.Kind == kind);

    private static bool Halts(ComponentActionStatus status) =>
        status is ComponentActionStatus.Failed
            or ComponentActionStatus.Denied
            or ComponentActionStatus.TimedOut
            or ComponentActionStatus.Cancelled;

    private static string WithNotRestarted(string message, IReadOnlyList<ComponentDefinition> skipped)
    {
        List<string> names = [.. skipped.Where(row => row.HostedLocally).Select(row => row.DisplayName)];
        string result = names.Count > 0
                            ? $"{message} {string.Format(NotRestartedFormat, string.Join(NameSeparator, names))}"
                            : message;
        return result;
    }

    private static ComponentActionResult FromServiceOutcome(ComponentKind kind,
                                                            ServiceActionOutcome outcome,
                                                            ServiceAction action,
                                                            string subject)
    {
        ComponentActionStatus status = outcome switch
                                       {
                                           ServiceActionOutcome.Succeeded => ComponentActionStatus.Succeeded,
                                           ServiceActionOutcome.Cancelled => ComponentActionStatus.Cancelled,
                                           ServiceActionOutcome.TimedOut => ComponentActionStatus.TimedOut,
                                           ServiceActionOutcome.UnsafeInstallLocation => ComponentActionStatus.Denied,
                                           _ => ComponentActionStatus.Failed
                                       };
        var result = new ComponentActionResult(kind, status, ServiceActionOutcomeText.Describe(outcome, action, subject));
        return result;
    }

    private static ComponentActionResult NotControllable(ComponentDefinition component, string format) =>
        new(component.Kind, ComponentActionStatus.NotControllable, string.Format(format, component.DisplayName));

    private void Log(string component, string action, ComponentActionResult result, bool hostedLocally)
    {
        LogLevel level = result.Status switch
                         {
                             ComponentActionStatus.Denied or ComponentActionStatus.TimedOut or ComponentActionStatus.Failed =>
                                 LogLevel.Warning,
                             ComponentActionStatus.NotControllable when hostedLocally => LogLevel.Warning,
                             _ => LogLevel.Information
                         };
        mLogger?.Log(level, OutcomeMessage, component, action, result.Status, result.Message);
    }

    private static string Verb(ServiceAction action) => action switch
                                                        {
                                                            ServiceAction.Start => StartVerb,
                                                            ServiceAction.Stop => StopVerb,
                                                            _ => RestartVerb
                                                        };

    private const string LogDirectoryName = "SaddleRAG";
    private const string LogsFolderName = "logs";
    private const string OllamaLogFileName = "ollama-launch.log";

    private const string StartVerb = "start";
    private const string StopVerb = "stop";
    private const string RestartVerb = "restart";
    private const string RestartAllVerb = "restart all";
    private const string AllComponentsName = "All components";
    private const string SubjectSeparator = " and ";
    private const string NameSeparator = ", ";

    private const string BusyText = "Another component action is still running. Try again when it finishes.";
    private const string RemoteFormat = "{0} runs on another computer; the tray controls only components on this computer.";
    private const string NoServiceFormat = "{0} is not running as a Windows service on this computer.";
    private const string NoAddressFormat = "No address is configured for {0}.";
    private const string StartedFormat = "{0} started.";
    private const string AlreadyRunningFormat = "{0} is already running.";
    private const string DoclingNotRegisteredText = "Register Docling from the tray first.";
    private const string NotRestartableFormat = "{0} cannot be started again ({1}), so it was not stopped.";
    private const string DoclingNotRegisteredReason = "it is not registered in the tray";
    private const string OllamaNoExecutableReason = "Ollama:ExecutablePath is not set in SaddleRAG's settings";
    private const string DoclingTimeoutFormat = "{0} did not report ready in time. Its output is in {1}.";
    private const string DoclingFailedFormat = "{0} could not be started. Check the registered command; its output is in {1}.";
    private const string NotRestartedFormat = "Not restarted: {0}.";

    private const string OutcomeMessage = "{Component} {Action}: {Status}. {Detail}";
    private const string UnexpectedFailureMessage = "{Component} {Action} failed unexpectedly";
}
