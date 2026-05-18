// RenderMode.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Detected rendering strategy for a site, determined by the
///     <c>RenderModeVoter</c> during the first pages of a crawl.
/// </summary>
public enum RenderMode
{
    /// <summary>
    ///     Vote is not yet complete (fewer than the required sample pages fetched).
    /// </summary>
    Unknown,

    /// <summary>
    ///     Server-side rendered: content is present in the initial HTML.
    ///     The Load-state wait can be skipped.
    /// </summary>
    SSR,

    /// <summary>
    ///     Single-page application: JS injects substantial content after load.
    ///     The Load-state wait is required.
    /// </summary>
    SPA
}
