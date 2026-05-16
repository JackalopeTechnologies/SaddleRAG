// IngestProgressFormatter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Cli.Handlers;

/// <summary>
///     Pure formatter for the saddlerag-cli ingest progress line. Lives
///     in a separate class so the carriage-return-overwrite progress
///     ticker (which is otherwise inseparable from the orchestrator call)
///     has a unit-tested format. The orchestration plumbing
///     (DI resolves, OllamaBootstrapper, IngestionOrchestrator.IngestAsync
///     itself) stays inline in Program.cs because it has no pure logic
///     to extract.
/// </summary>
public static class IngestProgressFormatter
{
    /// <summary>
    ///     Render the progress line that <c>saddlerag-cli ingest</c>
    ///     prints in-place on every <c>onProgress</c> callback from the
    ///     orchestrator. The leading carriage return is part of the
    ///     contract — it overwrites the previous line on a TTY without
    ///     scrolling. Tests trim it.
    /// </summary>
    public static string Format(ScrapeJobRecord progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var result =
            $"\rQueued: {progress.PagesQueued} | Crawled: {progress.PagesFetched} | " +
            $"Classified: {progress.PagesClassified} | Chunks: {progress.ChunksGenerated} | " +
            $"Searchable: {progress.ChunksCompleted} chunks ({progress.PagesCompleted} pages)";
        return result;
    }
}
