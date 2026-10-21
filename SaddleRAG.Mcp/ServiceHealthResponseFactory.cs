// ServiceHealthResponseFactory.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Ingestion.Documents.Docling;

#endregion

namespace SaddleRAG.Mcp;

internal static class ServiceHealthResponseFactory
{
    private const string HealthyStatus = "Healthy";
    private const string UnavailableStatus = "Unavailable";
    private const string StartingStatus = "Starting";
    private const string DegradedStatus = "Degraded";
    private const string FailedStatus = "Failed";
    private const string NotStartedStatus = "NotStarted";
    private const string RunningStatus = "Running";
    internal static string OverallStatus(McpWarmupState warmup, SaddleRAG.Core.DependencyHealthState dependencies)
    {
        var entries = dependencies.Snapshot();
        string result = HealthyStatus;
        if (entries.Count == 0 || warmup.Status is NotStartedStatus or RunningStatus)
            result = StartingStatus;
        if (entries.Any(entry => entry.Status == UnavailableStatus && !entry.Required))
            result = DegradedStatus;
        if (warmup.Status == FailedStatus || entries.Any(entry => entry.Status == UnavailableStatus && entry.Required))
            result = UnavailableStatus;
        return result;
    }

    internal static int HttpStatus(string status) =>
        status is UnavailableStatus or StartingStatus ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK;

    internal static IResult CreateResult(McpWarmupState warmupState,
                                         IDoclingCapabilityService doclingCapability,
                                         SaddleRAG.Core.DependencyHealthState dependencies,
                                         SaddleRAG.Core.Interfaces.IServerLogReader logReader,
                                         SaddleRAG.Core.Interfaces.IServerLogAcknowledgement acknowledgement)
    {
        string status = OverallStatus(warmupState, dependencies);
        if (status == HealthyStatus && doclingCapability.CurrentStatus.State == DoclingCapabilityState.Unavailable)
            status = DegradedStatus;
        var response = Create(status, warmupState, doclingCapability) with
        {
            Services = dependencies.Snapshot(),
            Notifications = LogNotificationReader.Read(logReader, acknowledgement)
        };
        return Results.Json(response, statusCode: HttpStatus(status));
    }

    public static ServiceHealthResponse Create(string serviceStatus,
                                               McpWarmupState warmupState,
                                               IDoclingCapabilityService capability)
    {
        ArgumentException.ThrowIfNullOrEmpty(serviceStatus);
        ArgumentNullException.ThrowIfNull(warmupState);
        ArgumentNullException.ThrowIfNull(capability);

        var status = capability.CurrentStatus;
        var documentIngestion = new DocumentIngestionHealthResponse(status.State.ToString(),
                                                                    status.ReasonCode,
                                                                    status.Detail,
                                                                    status.Endpoint,
                                                                    status.LastCheckedAt,
                                                                    status.Remediation);
        return new ServiceHealthResponse(serviceStatus,
                                         warmupState.Status,
                                         warmupState.CurrentPhase,
                                         warmupState.LastError,
                                         documentIngestion);
    }
}
