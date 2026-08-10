// DoclingLaunchSettings.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Where Docling answers and how long the tray waits for it. Deliberately a small
///     tray-local record rather than a reference to the ingestion assembly's
///     DoclingSettings, which would drag the whole ingestion stack into the tray.
/// </summary>
public sealed record DoclingLaunchSettings(Uri Endpoint, TimeSpan ReadinessTimeout, TimeSpan PollInterval)
{
    /// <summary>
    ///     Matches the ingestion default endpoint and its 120 s startup grace period. Cold
    ///     start is slow — the readiness probe alone can take well over 30 s, and model load
    ///     longer — so a short wait would report a false timeout.
    /// </summary>
    public static DoclingLaunchSettings CreateDefault() =>
        new(new Uri(DefaultEndpoint),
            TimeSpan.FromSeconds(DefaultReadinessTimeoutSeconds),
            TimeSpan.FromSeconds(DefaultPollIntervalSeconds));

    private const string DefaultEndpoint = "http://localhost:5001";
    private const int DefaultReadinessTimeoutSeconds = 120;
    private const int DefaultPollIntervalSeconds = 2;
}
