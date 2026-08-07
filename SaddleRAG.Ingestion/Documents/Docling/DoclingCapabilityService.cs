// DoclingCapabilityService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>
///     Exposes cached Docling capability state and an explicit refresh boundary.
/// </summary>
public sealed class DoclingCapabilityService : IDoclingCapabilityService
{
    public DoclingCapabilityService(DoclingReadinessProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        mProbe = probe;
    }

    private readonly DoclingReadinessProbe mProbe;
    private readonly object mSync = new();
    private DoclingCapabilityStatus? mRecordedFailure;
    private Task<DoclingCapabilityStatus>? mRefreshTask;

    public DoclingCapabilityStatus CurrentStatus
    {
        get
        {
            DoclingCapabilityStatus result;
            lock(mSync)
                result = mRecordedFailure ?? mProbe.CurrentStatus;
            return result;
        }
    }

    public Task<DoclingCapabilityStatus> GetStatusAsync(bool refresh = false,
                                                        CancellationToken cancellationToken = default)
    {
        Task<DoclingCapabilityStatus> result;
        if (refresh)
        {
            lock(mSync)
            {
                if (mRefreshTask == null || mRefreshTask.IsCompleted)
                    mRefreshTask = RefreshAsync(cancellationToken);
                result = mRefreshTask;
            }
        }
        else
        {
            result = Task.FromResult(CurrentStatus);
        }

        return result;
    }

    public void RecordUnexpectedFailure()
    {
        var previous = CurrentStatus;
        var status = new DoclingCapabilityStatus(DoclingCapabilityState.Unavailable,
                                                 DoclingReasonCodes.ProbeFailed,
                                                 UnexpectedFailureDetail,
                                                 previous.Endpoint,
                                                 DateTimeOffset.UtcNow,
                                                 UnexpectedFailureRemediation);
        lock(mSync)
            mRecordedFailure = status;
    }

    private async Task<DoclingCapabilityStatus> RefreshAsync(CancellationToken cancellationToken)
    {
        var result = await mProbe.ProbeAsync(cancellationToken);
        lock(mSync)
            mRecordedFailure = null;
        return result;
    }

    private const string UnexpectedFailureDetail = "The Docling capability check failed unexpectedly.";
    private const string UnexpectedFailureRemediation =
        "Review the SaddleRAG and user-managed Docling logs, then retry the status check.";
}
