// StatusHandler.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Interfaces;

#endregion

namespace SaddleRAG.Cli.Handlers;

/// <summary>
///     Renders the output of <c>saddlerag-cli status --library &lt;id&gt;</c>.
///     Extracted from the inline System.CommandLine SetAction lambda in
///     Program.cs so the per-version pages/chunks formatting can be
///     unit-tested against a <see cref="TextWriter" />.
/// </summary>
public static class StatusHandler
{
    /// <summary>
    ///     Render status for a single library. Returns the process exit
    ///     code (0 in both the found and not-found paths; not-found prints
    ///     a friendly message but still completes successfully because the
    ///     user's CLI invocation was well-formed).
    /// </summary>
    public static async Task<int> RunAsync(string libraryId,
                                           ILibraryRepository libraryRepository,
                                           IPageRepository pageRepository,
                                           IChunkRepository chunkRepository,
                                           TextWriter output,
                                           CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentNullException.ThrowIfNull(libraryRepository);
        ArgumentNullException.ThrowIfNull(pageRepository);
        ArgumentNullException.ThrowIfNull(chunkRepository);
        ArgumentNullException.ThrowIfNull(output);

        var library = await libraryRepository.GetLibraryAsync(libraryId, ct);
        if (library == null)
            await output.WriteLineAsync($"Library '{libraryId}' not found.");
        else
        {
            await output.WriteLineAsync($"Library: {library.Name} ({library.Id})");
            await output.WriteLineAsync($"Current Version: {library.CurrentVersion}");
            await output.WriteLineAsync($"All Versions: {string.Join(", ", library.AllVersions)}");

            foreach(var ver in library.AllVersions)
            {
                int pages = await pageRepository.GetPageCountAsync(libraryId, ver, ct);
                int chunks = await chunkRepository.GetChunkCountAsync(libraryId, ver, ct);
                await output.WriteLineAsync($"  v{ver}: {pages} pages, {chunks} chunks");
            }
        }

        return 0;
    }
}
