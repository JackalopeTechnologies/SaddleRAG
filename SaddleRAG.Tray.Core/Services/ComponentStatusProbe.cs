// ComponentStatusProbe.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Checks whether one component is up, without depending on the SaddleRAG service: the
///     window must still show every row when that service or MongoDB is down. Each check is
///     bounded by <see cref="ProbeTimeoutSeconds" /> seconds, and a check that fails says why in
///     <see cref="ComponentStatus.Detail" /> rather than in the log, because the window polls every
///     few seconds. Only an unexpected exception is logged, once per distinct message.
///     <para>
///         A component that runs on another computer is never contacted; its row reports
///         <see cref="ComponentState.Unknown" />.
///     </para>
///     <para>
///         Windows takes about two seconds to give up on each refused loopback connection, so
///         asking a closed port would spend most of the time limit. When an <see cref="IPortOwnerLookup" />
///         is supplied, a port with no listener is reported at once, from the listener table,
///         without opening a connection.
///     </para>
/// </summary>
public sealed class ComponentStatusProbe
{
    public ComponentStatusProbe(HttpClient http,
                                IServiceStateReader services,
                                TimeProvider? time = null,
                                IPortOwnerLookup? ports = null,
                                ILogger<ComponentStatusProbe>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(services);

        mHttp = http;
        mServices = services;
        mTime = time ?? TimeProvider.System;
        mPorts = ports;
        mLogger = logger;
    }

    /// <summary>The longest each component check may take.</summary>
    public const int ProbeTimeoutSeconds = 3;

    public static TimeSpan ProbeTimeout => TimeSpan.FromSeconds(ProbeTimeoutSeconds);

    private readonly HttpClient mHttp;
    private readonly IServiceStateReader mServices;
    private readonly TimeProvider mTime;
    private readonly IPortOwnerLookup? mPorts;
    private readonly ILogger<ComponentStatusProbe>? mLogger;
    private readonly HashSet<string> mLoggedMessages = [];
    private readonly Lock mLoggedMessagesLock = new();

    /// <summary>Checks one component and reports what it found.</summary>
    /// <param name="component">The row to check.</param>
    /// <param name="ct">Cancels the check; the cancellation propagates to the caller.</param>
    public async Task<ComponentStatus> ProbeAsync(ComponentDefinition component, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(component);
        ct.ThrowIfCancellationRequested();

        ComponentStatus result;
        if (component.HostedLocally)
            result = await ProbeLocalAsync(component, ct);
        else
            result = new ComponentStatus(component.Kind, ComponentState.Unknown, RemoteDetail);

        return result;
    }

    private async Task<ComponentStatus> ProbeLocalAsync(ComponentDefinition component, CancellationToken ct)
    {
        ComponentStatus result;
        using var timeout = new CancellationTokenSource(ProbeTimeout, mTime);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            result = await DispatchAsync(component, linked.Token);
        }
        catch(OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The caller did not cancel, so the time limit did: the component is not answering.
            result = new ComponentStatus(component.Kind, ComponentState.Unreachable, $"{NoAnswerDetail}{ProbeTimeoutSeconds} seconds.");
        }
        catch(Exception ex) when (ex is not OperationCanceledException)
        {
            // A poll must never take the window down, so an unexpected failure becomes an Unknown
            // row with the reason in its detail. The log hears about each distinct message once.
            LogOnce(ex);
            result = new ComponentStatus(component.Kind, ComponentState.Unknown, $"{CheckFailedDetail}{ex.Message}");
        }

        return result;
    }

    private Task<ComponentStatus> DispatchAsync(ComponentDefinition component, CancellationToken token) =>
        component.Kind switch
        {
            ComponentKind.SaddleRagService => ProbeSaddleRagAsync(component, token),
            ComponentKind.MongoDb => ProbeMongoAsync(component, token),
            ComponentKind.Docling => ProbeDoclingAsync(component, token),
            ComponentKind.Ollama => ProbeOllamaAsync(component, token),
            _ => Task.FromResult(new ComponentStatus(component.Kind, ComponentState.Unknown, UnsupportedDetail))
        };

    private async Task<ComponentStatus> ProbeSaddleRagAsync(ComponentDefinition component, CancellationToken token)
    {
        ComponentStatus? serviceVerdict = await CheckServiceAsync(component, token);
        ComponentStatus result = serviceVerdict ?? await ProbeSaddleRagHealthAsync(component, token);
        return result;
    }

    private async Task<ComponentStatus> ProbeSaddleRagHealthAsync(ComponentDefinition component, CancellationToken token)
    {
        // Reached when the service is running or no service name is known. A refused connection
        // means "stopped" only when no service said otherwise.
        bool serviceKnown = component.WindowsServiceName is not null;
        ComponentStatus result;
        if (component.Endpoint is not { } endpoint)
            result = new ComponentStatus(component.Kind, ComponentState.Running, ServiceRunningDetail);
        else
            result = await GetAsync(component,
                                    new Uri(endpoint, HealthPath),
                                    serviceKnown ? ComponentState.Unreachable : ComponentState.Stopped,
                                    serviceKnown ? ServiceNotAnsweringDetail : NothingListeningDetail,
                                    EvaluateSaddleRagHealth,
                                    token);

        return result;
    }

    private async Task<ComponentStatus> ProbeMongoAsync(ComponentDefinition component, CancellationToken token)
    {
        ComponentStatus? serviceVerdict = await CheckServiceAsync(component, token);
        bool serviceRunning = component.WindowsServiceName is not null && serviceVerdict is null;
        ComponentStatus result = serviceVerdict ?? await ProbeMongoPortAsync(component, serviceRunning, token);
        return result;
    }

    private async Task<ComponentStatus> ProbeMongoPortAsync(ComponentDefinition component,
                                                            bool serviceRunning,
                                                            CancellationToken token)
    {
        ComponentStatus result;
        if (component.Port is { } port)
            result = IsPortClosed(port)
                         ? NoListener(component, port, serviceRunning)
                         : await ConnectAsync(component, port, serviceRunning, token);
        else
            result = serviceRunning
                         ? new ComponentStatus(component.Kind, ComponentState.Running, ServiceRunningDetail)
                         : new ComponentStatus(component.Kind, ComponentState.Unknown, NoPortDetail);

        return result;
    }

    private Task<ComponentStatus> ProbeDoclingAsync(ComponentDefinition component, CancellationToken token) =>
        GetEndpointAsync(component, HealthPath, EvaluateDoclingHealth, token);

    private Task<ComponentStatus> ProbeOllamaAsync(ComponentDefinition component, CancellationToken token) =>
        GetEndpointAsync(component, OllamaVersionPath, EvaluateOllamaVersion, token);

    private Task<ComponentStatus> GetEndpointAsync(ComponentDefinition component,
                                                   string path,
                                                   Func<ComponentDefinition, HttpResponseMessage, string, ComponentStatus> evaluate,
                                                   CancellationToken token) =>
        component.Endpoint is { } endpoint
            ? GetAsync(component, new Uri(endpoint, path), ComponentState.Stopped, NothingListeningDetail, evaluate, token)
            : Task.FromResult(new ComponentStatus(component.Kind, ComponentState.Unknown, NoEndpointDetail));

    /// <summary>
    ///     Returns the verdict when the Windows service decides the answer by itself (not installed,
    ///     stopped, changing state), or null when the component has no known service or the service
    ///     is running and the caller should look further.
    /// </summary>
    private async Task<ComponentStatus?> CheckServiceAsync(ComponentDefinition component, CancellationToken token)
    {
        ComponentStatus? verdict = null;
        if (component.WindowsServiceName is { } serviceName)
        {
            // The service control manager call cannot be cancelled, so it runs on the pool and the
            // wait is abandoned at the time limit.
            McpServiceState state = await Task.Run(() => mServices.GetState(serviceName), token).WaitAsync(token);
            verdict = state switch
            {
                McpServiceState.Running => null,
                McpServiceState.Stopped => new ComponentStatus(component.Kind, ComponentState.Stopped, ServiceStoppedDetail),
                McpServiceState.NotInstalled => new ComponentStatus(component.Kind, ComponentState.NotInstalled, ServiceNotInstalledDetail),
                McpServiceState.Transitioning => new ComponentStatus(component.Kind, ComponentState.Unknown, ServiceChangingDetail),
                _ => new ComponentStatus(component.Kind, ComponentState.Unknown, ServiceUnreadableDetail)
            };
        }

        return verdict;
    }

    private async Task<ComponentStatus> GetAsync(ComponentDefinition component,
                                                 Uri address,
                                                 ComponentState refusedState,
                                                 string refusedDetail,
                                                 Func<ComponentDefinition, HttpResponseMessage, string, ComponentStatus> evaluate,
                                                 CancellationToken token)
    {
        ComponentStatus result;
        if (IsPortClosed(address.Port))
        {
            result = new ComponentStatus(component.Kind, refusedState, refusedDetail);
        }
        else
        {
            try
            {
                using HttpResponseMessage response = await mHttp.GetAsync(address, token);
                string body = await response.Content.ReadAsStringAsync(token);
                result = evaluate(component, response, body);
            }
            catch(HttpRequestException ex) when (IsConnectionRefused(ex))
            {
                result = new ComponentStatus(component.Kind, refusedState, refusedDetail);
            }
            catch(HttpRequestException ex)
            {
                result = new ComponentStatus(component.Kind, ComponentState.Unreachable, $"{RequestFailedDetail}{ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    ///     True only when a port lookup was supplied, it worked, and it found no listener. A lookup
    ///     that is absent or fails leaves the answer to a real connection attempt.
    /// </summary>
    private bool IsPortClosed(int port)
    {
        var closed = false;
        if (mPorts is not null)
        {
            try
            {
                closed = mPorts.FindListeningProcess(port) is null;
            }
            catch(Win32Exception)
            {
                // The listener table could not be read. That says nothing about the port, so the
                // caller falls back to connecting to it.
                closed = false;
            }
        }

        return closed;
    }

    private static ComponentStatus NoListener(ComponentDefinition component, int port, bool serviceRunning) =>
        serviceRunning
            ? new ComponentStatus(component.Kind, ComponentState.Unreachable, $"{PortRefusedDetail}{port}.")
            : new ComponentStatus(component.Kind, ComponentState.Stopped, $"{NothingOnPortDetail}{port}.");

    private static async Task<ComponentStatus> ConnectAsync(ComponentDefinition component,
                                                            int port,
                                                            bool serviceRunning,
                                                            CancellationToken token)
    {
        // "localhost" can name both loopback addresses, and connecting to them one after the other
        // would spend two refusals' worth of time. Both are tried together and the first to connect wins.
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        var attempts = new List<Task<bool>>
                       {
                           TryConnectAsync(IPAddress.Loopback, port, race.Token, token),
                           TryConnectAsync(IPAddress.IPv6Loopback, port, race.Token, token)
                       };
        var connected = false;
        while (attempts.Count > 0 && !connected)
        {
            Task<bool> finished = await Task.WhenAny(attempts);
            attempts.Remove(finished);
            connected = await finished;
        }

        await race.CancelAsync();
        ComponentStatus result = connected
                                     ? new ComponentStatus(component.Kind, ComponentState.Running, $"{AcceptingConnectionsDetail}{port}.")
                                     : NoListener(component, port, serviceRunning);
        return result;
    }

    private static async Task<bool> TryConnectAsync(IPAddress address, int port, CancellationToken raceToken, CancellationToken token)
    {
        var connected = false;
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port, raceToken);
            connected = true;
        }
        catch(SocketException)
        {
            // Refused, or this computer has no such loopback address: this address did not connect.
            connected = false;
        }
        catch(OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // The other address connected first and cancelled this attempt.
            connected = false;
        }

        return connected;
    }
    private static ComponentStatus EvaluateSaddleRagHealth(ComponentDefinition component,
                                                           HttpResponseMessage response,
                                                           string body)
    {
        // Any JSON body counts: the health endpoint answers HTTP 503 with a JSON report while it
        // is still starting or a dependency is down, and that still means the service is running.
        ComponentStatus result = HealthResponse.TryReadStatus(body, out string? status)
                                     ? new ComponentStatus(component.Kind,
                                                           ComponentState.Running,
                                                           status is null ? HealthAnsweredDetail : $"{HealthStatusDetail}{status}.")
                                     : new ComponentStatus(component.Kind, ComponentState.Unreachable, HealthNotJsonDetail);
        return result;
    }

    private static ComponentStatus EvaluateDoclingHealth(ComponentDefinition component,
                                                         HttpResponseMessage response,
                                                         string body)
    {
        ComponentStatus result;
        if (response.IsSuccessStatusCode)
        {
            // Docling's server is up before it can convert; only status "ok" means ready.
            HealthResponse.TryReadStatus(body, out string? status);
            result = HealthResponse.ReportsOk(body)
                         ? new ComponentStatus(component.Kind, ComponentState.Running, DoclingReadyDetail)
                         : new ComponentStatus(component.Kind,
                                               ComponentState.Unreachable,
                                               status is null ? DoclingNotReadyDetail : $"{DoclingStatusDetail}{status}.");
        }
        else
        {
            result = new ComponentStatus(component.Kind, ComponentState.Unreachable, $"{HttpStatusDetail}{(int) response.StatusCode}.");
        }

        return result;
    }

    private static ComponentStatus EvaluateOllamaVersion(ComponentDefinition component,
                                                         HttpResponseMessage response,
                                                         string body)
    {
        ComponentStatus result = response.StatusCode == HttpStatusCode.OK
                                     ? new ComponentStatus(component.Kind, ComponentState.Running, OllamaReadyDetail)
                                     : new ComponentStatus(component.Kind, ComponentState.Unreachable, $"{HttpStatusDetail}{(int) response.StatusCode}.");
        return result;
    }

    private static bool IsConnectionRefused(HttpRequestException ex) =>
        ex.HttpRequestError == HttpRequestError.ConnectionError
        || ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused };

    private void LogOnce(Exception ex)
    {
        bool firstTime;
        lock(mLoggedMessagesLock)
            firstTime = mLoggedMessages.Add(ex.Message);

        if (firstTime)
            mLogger?.LogWarning(ex, UnexpectedFailureMessage);
    }

    /// <summary>The detail every remote row carries; the tray never contacts a remote host.</summary>
    public const string RemoteDetail = "Hosted on another computer; see the SaddleRAG health page.";

    private const string HealthPath = "/health";
    private const string OllamaVersionPath = "/api/version";
    private const string NoAnswerDetail = "Did not answer within ";
    private const string CheckFailedDetail = "The status check failed: ";
    private const string UnsupportedDetail = "No status check exists for this component.";
    private const string ServiceRunningDetail = "The Windows service is running.";
    private const string ServiceStoppedDetail = "The Windows service is stopped.";
    private const string ServiceNotInstalledDetail = "The Windows service is not installed.";
    private const string ServiceChangingDetail = "The Windows service is starting or stopping.";
    private const string ServiceUnreadableDetail = "The state of the Windows service could not be read.";
    private const string ServiceNotAnsweringDetail = "The Windows service is running but its health endpoint is not answering.";
    private const string NothingListeningDetail = "Nothing is listening at the configured address.";
    private const string NoEndpointDetail = "No address is configured for this component.";
    private const string NoPortDetail = "No port or Windows service is configured for this component.";
    private const string AcceptingConnectionsDetail = "Accepting connections on port ";
    private const string PortRefusedDetail = "The Windows service is running but refused a connection on port ";
    private const string NothingOnPortDetail = "Nothing is listening on port ";
    private const string RequestFailedDetail = "The request failed: ";
    private const string HealthAnsweredDetail = "The health endpoint answered.";
    private const string HealthStatusDetail = "The health endpoint reports ";
    private const string HealthNotJsonDetail = "The service is running but its health endpoint did not return a health report.";
    private const string HttpStatusDetail = "Answered with HTTP status ";
    private const string DoclingReadyDetail = "Docling reports ready.";
    private const string DoclingNotReadyDetail = "Docling answered but does not report ready.";
    private const string DoclingStatusDetail = "Docling answered but reports status ";
    private const string OllamaReadyDetail = "Ollama is answering.";
    private const string UnexpectedFailureMessage = "A component status check failed unexpectedly";
}