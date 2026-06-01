// ReclassifyHandler.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;

#endregion

namespace SaddleRAG.Cli.Handlers;

/// <summary>
///     Runs <c>saddlerag-cli reclassify</c>. Iterates one library (or all
///     libraries when <c>libraryId</c> is null/empty), classifies each
///     page via <see cref="ILlmClassifier" />, persists the new category
///     when it differs and confidence is non-zero. Extracted from
///     Program.cs so the iteration logic, the all-pages-vs-unclassified
///     filter, the progress cadence, and the reclassified-counter are
///     unit-testable. Ollama bootstrap stays in Program.cs because it's
///     one-time setup, not handler logic.
/// </summary>
public static class ReclassifyHandler
{
    /// <summary>
    ///     Reclassify pages for the configured library scope. Returns 0
    ///     on success. Progress lines and the final summary are written to
    ///     <paramref name="output" />.
    /// </summary>
    public static async Task<int> RunAsync(string? libraryId,
                                           bool allPages,
                                           ILibraryRepository libraryRepository,
                                           IPageRepository pageRepository,
                                           IChunkRepository chunkRepository,
                                           ILlmClassifier classifier,
                                           TextWriter output,
                                           CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(libraryRepository);
        ArgumentNullException.ThrowIfNull(pageRepository);
        ArgumentNullException.ThrowIfNull(chunkRepository);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(output);

        IReadOnlyList<LibraryRecord> libraries;
        if (string.IsNullOrEmpty(libraryId))
            libraries = await libraryRepository.GetAllLibrariesAsync(ct);
        else
        {
            var single = await libraryRepository.GetLibraryAsync(libraryId, ct);
            libraries = single is null
                            ? throw new InvalidOperationException($"Library '{libraryId}' not found")
                            : [single];
        }

        var totalProcessed = 0;
        var totalReclassified = 0;

        foreach(var lib in libraries)
        {
            await output.WriteLineAsync($"\nReclassifying {lib.Id} v{lib.CurrentVersion}...");
            var pages = await pageRepository.GetPagesAsync(lib.Id, lib.CurrentVersion, ct);
            var targetPages = allPages
                                  ? pages.ToList()
                                  : pages.Where(p => p.Category == DocCategory.Unclassified).ToList();

            await output.WriteLineAsync($"  {targetPages.Count} pages to process (of {pages.Count} total)");

            var processed = 0;
            foreach(var page in targetPages)
            {
                (var newCategory, var confidence) = await classifier.ClassifyAsync(page, lib.Hint, ct);
                processed++;

                if (newCategory != DocCategory.Unclassified &&
                    confidence > 0 &&
                    newCategory != page.Category)
                {
                    var classified = page with { Category = newCategory };
                    await pageRepository.UpsertPageAsync(classified, ct);
                    await chunkRepository.UpdateCategoryByPageUrlAsync(lib.Id,
                                                                      lib.CurrentVersion,
                                                                      page.Url,
                                                                      newCategory,
                                                                      ct
                                                                     );
                    totalReclassified++;
                }

                if (processed % ProgressEveryNPages == 0)
                {
                    await output
                        .WriteLineAsync($"  {processed}/{targetPages.Count} processed, {totalReclassified} reclassified so far"
                                       );
                }
            }

            totalProcessed += processed;
        }

        await output.WriteLineAsync($"\nDone. Processed {totalProcessed} pages, reclassified {totalReclassified}.");
        await output
            .WriteLineAsync("Pages and chunks updated in MongoDB. Restart MCP server (or call reload_profile) to refresh in-memory index."
                           );

        return 0;
    }

    private const int ProgressEveryNPages = 10;
}
