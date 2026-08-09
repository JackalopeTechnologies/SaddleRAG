// DoclingLaunchRequestService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Ingestion.Documents.Docling;

#endregion

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Answers whether a directory scan is waiting while Docling is not ready.
///     <para>
///         Deliberately cheap: it reads the already-cached capability status and never
///         forces a probe refresh, because the tray polls this on a timer while running
///         under EcoQoS as an idle controller.
///     </para>
/// </summary>
public sealed class DoclingLaunchRequestService : IDoclingLaunchRequestService
{
    public DoclingLaunchRequestService(IDirectoryLibraryMonitorDataService libraries,
                                       IJobRepository jobs,
                                       IDoclingCapabilityService capability)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(capability);

        mLibraries = libraries;
        mJobs = jobs;
        mCapability = capability;
    }

    private readonly IDirectoryLibraryMonitorDataService mLibraries;
    private readonly IJobRepository mJobs;
    private readonly IDoclingCapabilityService mCapability;

    public async Task<DoclingLaunchRequestStatus> GetAsync(string? profile, CancellationToken ct = default)
    {
        var result = new DoclingLaunchRequestStatus(LaunchRequested: false, NotRequiredReasonCode);
        DoclingCapabilityStatus capability = mCapability.CurrentStatus;
        if (capability.State != DoclingCapabilityState.Ready)
        {
            IReadOnlyList<DirectoryLibraryMonitorRow> libraries = await mLibraries.ListAsync(profile, ct);
            if (await AnyScanWaitingAsync(libraries, ct))
                result = new DoclingLaunchRequestStatus(LaunchRequested: true, ResolveReasonCode(capability));
        }

        return result;
    }

    private async Task<bool> AnyScanWaitingAsync(IReadOnlyList<DirectoryLibraryMonitorRow> libraries,
                                                  CancellationToken ct)
    {
        var waiting = false;
        var index = 0;
        while (!waiting && index < libraries.Count)
        {
            IReadOnlyList<JobRecord> active = await mJobs.ListActiveAsync(libraries[index].LibraryId,
                                                                          version: null,
                                                                          JobType.DirectoryScan,
                                                                          ct);
            waiting = active.Count > 0;
            index++;
        }

        return waiting;
    }

    private static string ResolveReasonCode(DoclingCapabilityStatus capability) =>
        string.IsNullOrWhiteSpace(capability.ReasonCode) ? RequestedReasonCode : capability.ReasonCode;

    private const string NotRequiredReasonCode = "DOCLING_LAUNCH_NOT_REQUIRED";
    private const string RequestedReasonCode = "DOCLING_LAUNCH_REQUESTED";
}
