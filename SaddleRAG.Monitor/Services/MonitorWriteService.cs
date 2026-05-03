// MonitorWriteService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Client-side HTTP service for the /api/monitor write endpoints.
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

    private const string CancelJobUrlTemplate = "/api/monitor/jobs/{0}/cancel";

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
}
