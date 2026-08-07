// HttpInstallerHealthProbe.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Net;

namespace SaddleRAG.Installer.Helper;

/// <summary>Performs a bounded HTTP GET and accepts only an HTTP 200 response.</summary>
public sealed class HttpInstallerHealthProbe : IInstallerHealthProbe
{
    public HttpInstallerHealthProbe(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        mHttpClient = httpClient;
    }

    private readonly HttpClient mHttpClient;

    public async Task<bool> IsHealthyAsync(Uri healthUrl,
                                           TimeSpan requestTimeout,
                                           CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(healthUrl);
        if (requestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout),
                                                  requestTimeout,
                                                  "The request timeout must be positive.");

        var result = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        try
        {
            using HttpResponseMessage response = await mHttpClient.GetAsync(healthUrl, timeout.Token);
            result = response.StatusCode == HttpStatusCode.OK;
        }
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested)
        {
            result = false;
        }
        catch(HttpRequestException)
        {
            result = false;
        }

        return result;
    }
}
