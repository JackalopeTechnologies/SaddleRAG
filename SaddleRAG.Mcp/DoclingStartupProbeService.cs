// DoclingStartupProbeService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Ingestion.Documents.Docling;

#endregion

namespace SaddleRAG.Mcp;

/// <summary>Performs one optional Docling capability refresh after host startup.</summary>
public sealed class DoclingStartupProbeService : BackgroundService
{
    public DoclingStartupProbeService(IDoclingCapabilityService capability,
                                      ILogger<DoclingStartupProbeService> logger)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(logger);
        mCapability = capability;
        mLogger = logger;
    }

    private readonly IDoclingCapabilityService mCapability;
    private readonly ILogger<DoclingStartupProbeService> mLogger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        await ProbeOnceAsync(stoppingToken);
    }

    internal async Task ProbeOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await mCapability.GetStatusAsync(refresh: true, cancellationToken);
            if (status.State == DoclingCapabilityState.Ready)
            {
                mLogger.LogInformation("Optional document ingestion is ready at {Endpoint}.", status.Endpoint);
            }
            else
            {
                mLogger.LogWarning("Optional document ingestion is {State}: {ReasonCode}. {Detail}",
                                   status.State,
                                   status.ReasonCode,
                                   status.Detail);
            }
        }
        catch(OperationCanceledException)
        {
            throw;
        }
        catch(Exception)
        {
            mCapability.RecordUnexpectedFailure();
            mLogger.LogWarning("Optional document ingestion startup check failed unexpectedly; SaddleRAG remains available.");
        }
    }
}
