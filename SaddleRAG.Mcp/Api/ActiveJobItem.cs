// ActiveJobItem.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial

namespace SaddleRAG.Mcp.Api;

/// <summary>One running scrape/ingest job in a <see cref="StatusResponse" />.</summary>
/// <param name="Id">Unique job identifier (GUID string).</param>
/// <param name="Library">Library identifier (<see cref="SaddleRAG.Core.Models.ScrapeJob.LibraryId" />).</param>
/// <param name="Phase">Pipeline phase string (e.g. "Scraping", "Embedding").</param>
public sealed record ActiveJobItem(string Id, string Library, string Phase);
