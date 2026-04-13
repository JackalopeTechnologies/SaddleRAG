// // LibraryTools.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using DocRAG.Core.Interfaces;
using DocRAG.Database.Repositories;
using ModelContextProtocol.Server;

#endregion

namespace DocRAG.Mcp.Tools;

/// <summary>
///     MCP tools for listing and querying available documentation libraries.
/// </summary>
[McpServerToolType]
public static class LibraryTools
{
    [McpServerTool(Name = "list_libraries")]
    [Description("List all available documentation libraries with their current version and all ingested versions.")]
    public static async Task<string> ListLibraries(RepositoryFactory repositoryFactory,
                                                   [Description("Optional database profile name (use list_profiles to discover)"
                                                               )]
                                                   string? profile = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);

        var libraryRepository = repositoryFactory.GetLibraryRepository(profile);
        var libraries = await libraryRepository.GetAllLibrariesAsync(ct);
        var result = JsonSerializer.Serialize(libraries, new JsonSerializerOptions { WriteIndented = true });
        return result;
    }

    [McpServerTool(Name = "list_classes")]
    [Description("List all documented classes/types for a library. Useful for discovering what API reference is available."
                )]
    public static async Task<string> ListClasses(RepositoryFactory repositoryFactory,
                                                 [Description("Library identifier (e.g. 'infragistics-wpf', 'questpdf')"
                                                             )]
                                                 string library,
                                                 [Description("Optional partial name filter")]
                                                 string? filter = null,
                                                 [Description("Specific version â€” defaults to current")]
                                                 string? version = null,
                                                 [Description("Optional database profile name")]
                                                 string? profile = null,
                                                 CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);

        var libraryRepository = repositoryFactory.GetLibraryRepository(profile);
        var chunkRepository = repositoryFactory.GetChunkRepository(profile);

        var resolvedVersion = await ResolveVersionAsync(libraryRepository, library, version, ct);
        string result;
        if (resolvedVersion == null)
        {
            var notFound = new { Error = $"Library '{library}' not found." };
            result = JsonSerializer.Serialize(notFound, new JsonSerializerOptions { WriteIndented = true });
        }
        else
        {
            var names = await chunkRepository.GetQualifiedNamesAsync(library, resolvedVersion, filter, ct);
            result = JsonSerializer.Serialize(names, new JsonSerializerOptions { WriteIndented = true });
        }

        return result;
    }

    internal static async Task<string?> ResolveVersionAsync(ILibraryRepository libraryRepository,
                                                            string libraryId,
                                                            string? version,
                                                            CancellationToken ct)
    {
        string? result;

        if (!string.IsNullOrEmpty(version))
            result = version;
        else
        {
            var library = await libraryRepository.GetLibraryAsync(libraryId, ct);
            result = library?.CurrentVersion;
        }

        return result;
    }
}
