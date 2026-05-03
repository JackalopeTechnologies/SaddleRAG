// MonitorWriteService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings
using System.Net.Http.Json;
using SaddleRAG.Core.Models.Monitor;
#endregion

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Client-side HTTP service for the /api/monitor endpoints.
/// </summary>
public sealed class MonitorWriteService
{
    /// <summary>
    ///     Initializes a new instance of <see cref="MonitorWriteService"/>.
    /// </summary>
    public MonitorWriteService(HttpClient http)
    {
        mHttp = http;
    }

    private readonly HttpClient mHttp;

    private const string CancelJobUrlTemplate  = "/api/monitor/jobs/{0}/cancel";
    private const string SnapshotUrlTemplate   = "/api/monitor/jobs/{0}/snapshot";

    /// <summary>
    ///     Sends a cancel request for the given job. Returns <c>true</c> if the server accepted it.
    /// </summary>
    public async Task<bool> CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        var url      = string.Format(CancelJobUrlTemplate, jobId);
        var response = await mHttp.PostAsync(url, content: null, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    ///     Fetches the current in-memory snapshot for a job. Returns <c>null</c> if the job is not active.
    /// </summary>
    public async Task<JobTickSnapshot?> GetJobSnapshotAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        var url      = string.Format(SnapshotUrlTemplate, jobId);
        var response = await mHttp.GetAsync(url, ct);
        JobTickSnapshot? result = null;
        if (response.IsSuccessStatusCode)
            result = await response.Content.ReadFromJsonAsync<JobTickSnapshot>(ct);
        return result;
    }
}
