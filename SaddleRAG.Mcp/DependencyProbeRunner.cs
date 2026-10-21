// DependencyProbeRunner.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core;
using SaddleRAG.Core.Models;

namespace SaddleRAG.Mcp;

/// <summary>Applies a time limit and records failure without stopping the health host.</summary>
internal static class DependencyProbeRunner
{
    private const string HealthyStatus = "Healthy";
    private const string UnavailableStatus = "Unavailable";
    private const string TimeoutDetail = "The service did not respond before the check timed out.";
    internal static async Task CheckAsync(DependencyProbe probe, DependencyHealthState state,
                                          TimeSpan timeout, ILogger logger, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        DependencyHealth observation;
        try
        {
            string detail = await probe.Check(bounded.Token);
            observation = new(probe.Name, HealthyStatus, probe.Required, probe.AffectedWork,
                              detail, probe.Remediation, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            string detail = exception is OperationCanceledException
                ? TimeoutDetail
                : $"The service check failed ({exception.GetType().Name}).";
            observation = new(probe.Name, UnavailableStatus, probe.Required, probe.AffectedWork,
                              detail, probe.Remediation, DateTimeOffset.UtcNow);
        }

        if (state.Record(observation))
        {
            if (observation.Status == UnavailableStatus)
                logger.LogWarning("{Service} is unavailable. Affected work: {Work}. {Remediation}",
                                  observation.Name, observation.AffectedWork, observation.Remediation);
            else
                logger.LogInformation("{Service} is available", observation.Name);
        }
    }
}
