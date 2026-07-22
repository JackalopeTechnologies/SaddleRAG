// IPlaywrightRuntimeProbe.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License in the repo root.

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Ensures that the Playwright browser runtime required for web crawling
///     is installed and can launch on the current host.
/// </summary>
public interface IPlaywrightRuntimeProbe
{
    /// <summary>
    ///     Launches and closes headless Chromium. When the browser revision
    ///     pinned by the bundled Playwright driver is absent, installs it and
    ///     verifies the retry.
    /// </summary>
    Task VerifyAsync(CancellationToken ct = default);
}