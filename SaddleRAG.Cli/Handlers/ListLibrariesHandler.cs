// ListLibrariesHandler.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Interfaces;

#endregion

namespace SaddleRAG.Cli.Handlers;

/// <summary>
///     Renders the output of <c>saddlerag-cli list</c>. Extracted from the
///     inline System.CommandLine SetAction lambda in Program.cs so the
///     "empty libraries" vs "non-empty libraries" output formatting is
///     unit-testable against a <see cref="TextWriter" /> rather than
///     console output. The Program.cs lambda now just resolves
///     <see cref="ILibraryRepository" /> from DI and delegates here.
/// </summary>
public static class ListLibrariesHandler
{
    /// <summary>
    ///     Run the list command. Returns the process exit code (0 on
    ///     success). All output is written to <paramref name="output" /> so
    ///     tests can capture it via <see cref="StringWriter" />.
    /// </summary>
    public static async Task<int> RunAsync(ILibraryRepository libraryRepository,
                                           TextWriter output,
                                           CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(libraryRepository);
        ArgumentNullException.ThrowIfNull(output);

        var libraries = await libraryRepository.GetAllLibrariesAsync(ct);

        if (libraries.Count == 0)
            await output.WriteLineAsync(NoLibrariesMessage);
        else
        {
            foreach(var lib in libraries)
            {
                await output
                    .WriteLineAsync($"  {lib.Id} — {lib.Name} (current: {lib.CurrentVersion}, versions: {string.Join(", ", lib.AllVersions)})"
                                   );
            }
        }

        return 0;
    }

    internal const string NoLibrariesMessage = "No libraries ingested yet.";
}
