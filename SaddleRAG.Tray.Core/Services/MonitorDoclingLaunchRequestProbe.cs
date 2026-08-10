// MonitorDoclingLaunchRequestProbe.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Reads the launch-request flag from the Monitor's localhost HTTP boundary — the
///     transport the tray and service already share, rather than a new IPC channel.
/// </summary>
public sealed class MonitorDoclingLaunchRequestProbe : IDoclingLaunchRequestProbe
{
    public MonitorDoclingLaunchRequestProbe(HttpClient httpClient, Uri? monitorBaseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        mHttpClient = httpClient;
        mBaseAddress = monitorBaseAddress ?? new Uri(DefaultMonitorAddress);
    }

    private static readonly JsonSerializerOptions smJsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HttpClient mHttpClient;
    private readonly Uri mBaseAddress;

    public async Task<bool> IsLaunchRequestedAsync(CancellationToken ct = default)
    {
        var requested = false;
        using HttpResponseMessage response =
            await mHttpClient.GetAsync(new Uri(mBaseAddress, LaunchRequestPath), ct);
        if (response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            LaunchRequestPayload? payload = JsonSerializer.Deserialize<LaunchRequestPayload>(body, smJsonOptions);
            requested = payload?.LaunchRequested ?? false;
        }

        return requested;
    }

    private sealed record LaunchRequestPayload(bool LaunchRequested, string? ReasonCode);

    private const string DefaultMonitorAddress = "http://localhost:6100";
    private const string LaunchRequestPath = "/api/monitor/docling-launch-request";
}
