// OllamaServeLauncher.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Diagnostics;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Starts <c>ollama serve</c> from the tray's own (unelevated) session, on the address and with
///     the model folder from SaddleRAG's settings, and waits for it to answer.
///     <para>
///         A server is started only when nothing listens on Ollama's port. Ollama's desktop app
///         restarts <c>ollama serve</c> by itself when it is killed, and a second server would fight
///         it for the port; when something is already listening, the tray waits for it to answer.
///     </para>
/// </summary>
internal sealed class OllamaServeLauncher
{
    public OllamaServeLauncher(ComponentStatusProbe probe, IProcessStarter starter, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(starter);
        ArgumentNullException.ThrowIfNull(time);

        mProbe = probe;
        mStarter = starter;
        mTime = time;
    }

    /// <summary>How long Ollama is given to answer after it is started.</summary>
    public const int ReadyWaitSeconds = 60;

    /// <summary>How often Ollama is asked whether it is answering while the tray waits.</summary>
    public const int PollSeconds = 1;

    private readonly ComponentStatusProbe mProbe;
    private readonly IProcessStarter mStarter;
    private readonly TimeProvider mTime;

    /// <summary>Makes sure Ollama is answering, starting it if it is not.</summary>
    /// <param name="component">The Ollama row, carrying the executable and model folder.</param>
    /// <param name="ct">Cancels the checks and the wait; a started process keeps running.</param>
    public async Task<ComponentActionResult> StartAsync(ComponentDefinition component, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(component);

        ComponentStatus status = await mProbe.ProbeAsync(component, ct);
        ComponentActionResult result = status.State switch
                                       {
                                           ComponentState.Running =>
                                               Result(component,
                                                      ComponentActionStatus.Succeeded,
                                                      string.Format(AlreadyRunningFormat, component.DisplayName)),
                                           ComponentState.Stopped => await StartServeAsync(component, ct),

                                           // Something holds the port but is not answering yet, such as a
                                           // respawned server still loading. A second server could not bind
                                           // the port, so the tray waits for this one instead.
                                           _ => await WaitForRunningAsync(component, NotAnsweringFormat, ct)
                                       };
        return result;
    }

    private async Task<ComponentActionResult> StartServeAsync(ComponentDefinition component, CancellationToken ct)
    {
        ComponentActionResult result;
        if (string.IsNullOrWhiteSpace(component.ExecutablePath))
        {
            result = Result(component, ComponentActionStatus.Failed, NoExecutableText);
        }
        else
        {
            string? failure = Launch(component.ExecutablePath, component.ModelsDirectory, component.Endpoint);
            result = failure is null
                         ? await WaitForRunningAsync(component, StartedNotAnsweringFormat, ct)
                         : Result(component,
                                  ComponentActionStatus.Failed,
                                  string.Format(StartFailedFormat, component.ExecutablePath, failure));
        }

        return result;
    }

    private string? Launch(string executablePath, string? modelsDirectory, Uri? endpoint)
    {
        string? failure = null;
        try
        {
            // The handle is deliberately not disposed: disposing the Process would stop the drain of
            // its output into the launch log, and Ollama keeps running after this action ends.
            _ = mStarter.Start(BuildStartInfo(executablePath, modelsDirectory, endpoint));
        }
        catch(Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException
                                      or UnauthorizedAccessException)
        {
            failure = ex.Message;
        }

        return failure;
    }

    private static ProcessStartInfo BuildStartInfo(string executablePath, string? modelsDirectory, Uri? endpoint)
    {
        // UseShellExecute is false, so no "runas" verb can apply: Ollama always starts as the
        // signed-in user, never elevated.
        ProcessStartInfo startInfo = new()
                                     {
                                         FileName = executablePath,
                                         Arguments = ServeArgument,
                                         UseShellExecute = false,
                                         CreateNoWindow = true,
                                         RedirectStandardOutput = true,
                                         RedirectStandardError = true
                                     };
        if (!string.IsNullOrWhiteSpace(modelsDirectory))
            startInfo.Environment[ModelsVariable] = modelsDirectory;

        // ollama serve listens where OLLAMA_HOST says (127.0.0.1:11434 when unset), so it is told the
        // address SaddleRAG is configured to use; otherwise a non-default port would never answer.
        // Uri.Host keeps an IPv6 address in brackets, which OLLAMA_HOST expects.
        if (endpoint is not null)
            startInfo.Environment[HostVariable] = $"{endpoint.Host}:{endpoint.Port}";

        return startInfo;
    }

    private async Task<ComponentActionResult> WaitForRunningAsync(ComponentDefinition component,
                                                                  string timedOutFormat,
                                                                  CancellationToken ct)
    {
        DateTimeOffset deadline = mTime.GetUtcNow() + TimeSpan.FromSeconds(ReadyWaitSeconds);
        var running = false;
        while (!running && mTime.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(PollSeconds), mTime, ct);
            ComponentStatus status = await mProbe.ProbeAsync(component, ct);
            running = status.State == ComponentState.Running;
        }

        ComponentActionResult result = running
                                           ? Result(component,
                                                    ComponentActionStatus.Succeeded,
                                                    string.Format(StartedFormat, component.DisplayName))
                                           : Result(component,
                                                    ComponentActionStatus.TimedOut,
                                                    string.Format(timedOutFormat,
                                                                  component.DisplayName,
                                                                  ReadyWaitSeconds,
                                                                  ComponentController.OllamaLaunchLogPath));
        return result;
    }

    private static ComponentActionResult Result(ComponentDefinition component, ComponentActionStatus status, string message) =>
        new(component.Kind, status, message);

    private const string ServeArgument = "serve";
    private const string ModelsVariable = "OLLAMA_MODELS";
    private const string HostVariable = "OLLAMA_HOST";
    private const string AlreadyRunningFormat = "{0} is already running.";
    private const string StartedFormat = "{0} started.";
    private const string NoExecutableText = "Set Ollama:ExecutablePath in SaddleRAG's settings.";
    private const string StartFailedFormat = "Could not start {0}: {1}";
    private const string StartedNotAnsweringFormat = "{0} did not answer within {1} seconds of starting. Its output is in {2}.";
    private const string NotAnsweringFormat =
        "Something is listening on {0}'s port but did not answer within {1} seconds. Restart {0} to replace it.";
}
