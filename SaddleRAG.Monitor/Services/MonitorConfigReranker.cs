// MonitorConfigReranker.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Reranker card on the Monitor /config page (issue #73).
///     <see cref="Strategy" /> is the string form of
///     <c>RankingSettings.ReRankerStrategy</c>; <see cref="ActiveModel" />
///     is null when the OnnxSettings sentinel "none" is configured.
/// </summary>
public sealed record MonitorConfigReranker(
    string Strategy,
    string? ActiveModel,
    bool OnnxEnabled);
