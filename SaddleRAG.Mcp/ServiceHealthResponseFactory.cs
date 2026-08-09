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
