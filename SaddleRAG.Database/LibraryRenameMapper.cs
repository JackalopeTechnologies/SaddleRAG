// LibraryRenameMapper.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Security.Cryptography;
using System.Text;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Database;

/// <summary>Canonical identity remapping shared by Mongo and vector rename paths.</summary>
internal static class LibraryRenameMapper
{
    internal static DirectoryLibraryDefinition MapDirectoryDefinition(DirectoryLibraryDefinition source,
                                                                       string targetLibraryId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(targetLibraryId);
        var result = source with { Id = targetLibraryId };
        return result;
    }

    internal static DirectoryLibraryDefinition MapDirectoryDefinition(DirectoryLibraryDefinition source,
                                                                       string targetLibraryId,
                                                                       string sourceVersion,
                                                                       string targetVersion)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateTarget(targetLibraryId, targetVersion);
        var result = source with
                         {
                             Id = targetLibraryId,
                             LastPublishedVersion = MapVersion(source.LastPublishedVersion,
                                                               sourceVersion,
                                                               targetVersion)
                         };
        return result;
    }

    internal static SourceDocumentRecord MapSourceDocument(SourceDocumentRecord source,
                                                            string targetLibraryId,
                                                            string sourceVersion,
                                                            string targetVersion)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateTarget(targetLibraryId, targetVersion);
        string sourceUri = MapSourceUri(source.SourceUri, targetLibraryId, source.Id);
        var result = source with
                         {
                             LibraryId = targetLibraryId,
                             SourceUri = sourceUri,
                             FirstSeenVersion = MapVersion(source.FirstSeenVersion,
                                                           sourceVersion,
                                                           targetVersion) ?? source.FirstSeenVersion,
                             LastSeenVersion = MapVersion(source.LastSeenVersion,
                                                          sourceVersion,
                                                          targetVersion)
                         };
        return result;
    }

    internal static SourceDocumentRecord MapSourceDocument(SourceDocumentRecord source,
                                                            string targetLibraryId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(targetLibraryId);
        string sourceUri = MapSourceUri(source.SourceUri, targetLibraryId, source.Id);
        var result = source with
                         {
                             LibraryId = targetLibraryId,
                             SourceUri = sourceUri
                         };
        return result;
    }

    internal static DocumentRevisionRecord MapDocumentRevision(DocumentRevisionRecord source,
                                                                string targetLibraryId,
                                                                string targetVersion)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateTarget(targetLibraryId, targetVersion);
        var result = source with
                         {
                             Id = SourceDocumentRepository.MakeRevisionId(targetLibraryId,
                                                                           targetVersion,
                                                                           source.DocumentId),
                             LibraryId = targetLibraryId,
                             Version = targetVersion
                         };
        return result;
    }

    internal static SubjectCatalogRecord MapSubjectCatalog(SubjectCatalogRecord source,
                                                            string targetLibraryId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(targetLibraryId);
        var result = source with
                         {
                             Id = SubjectCatalogRepository.MakeId(targetLibraryId,
                                                                  source.TaxonomyVersion),
                             LibraryId = targetLibraryId
                         };
        return result;
    }

    internal static SubjectAssignmentRecord MapSubjectAssignment(SubjectAssignmentRecord source,
                                                                  string targetLibraryId,
                                                                  string targetVersion)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateTarget(targetLibraryId, targetVersion);
        string revisionId = SourceDocumentRepository.MakeRevisionId(targetLibraryId,
                                                                     targetVersion,
                                                                     source.DocumentId);
        var result = source with
                         {
                             Id = SubjectAssignmentRepository.MakeId(targetLibraryId,
                                                                     targetVersion,
                                                                     revisionId),
                             LibraryId = targetLibraryId,
                             Version = targetVersion,
                             DocumentRevisionId = revisionId
                         };
        return result;
    }

    internal static PageRecord MapPage(PageRecord source,
                                       string targetLibraryId,
                                       string targetVersion)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateTarget(targetLibraryId, targetVersion);
        DocumentProvenance? provenance = MapProvenance(source.DocumentSource,
                                                       targetLibraryId,
                                                       targetVersion);
        var result = source with
                         {
                             Id = MapScopedId(source.Id,
                                              source.LibraryId,
                                              source.Version,
                                              targetLibraryId,
                                              targetVersion,
                                              PageIdPrefix),
                             LibraryId = targetLibraryId,
                             Version = targetVersion,
                             Url = MapLocalUrl(source.Url, source.DocumentSource, provenance) ?? source.Url,
                             ParentUrl = MapLocalUrl(source.ParentUrl, source.DocumentSource, provenance),
                             DocumentSource = provenance
                         };
        return result;
    }

    internal static DocChunk MapChunk(DocChunk source,
                                      string targetLibraryId,
                                      string targetVersion)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateTarget(targetLibraryId, targetVersion);
        DocumentProvenance? provenance = MapProvenance(source.DocumentSource,
                                                       targetLibraryId,
                                                       targetVersion);
        var result = source with
                         {
                             Id = MapScopedId(source.Id,
                                              source.LibraryId,
                                              source.Version,
                                              targetLibraryId,
                                              targetVersion,
                                              ChunkIdPrefix),
                             LibraryId = targetLibraryId,
                             Version = targetVersion,
                             PageUrl = MapLocalUrl(source.PageUrl, source.DocumentSource, provenance) ??
                                       source.PageUrl,
                             ParentUrl = MapLocalUrl(source.ParentUrl, source.DocumentSource, provenance),
                             DocumentSource = provenance
                         };
        return result;
    }

    private static DocumentProvenance? MapProvenance(DocumentProvenance? source,
                                                     string targetLibraryId,
                                                     string targetVersion)
    {
        DocumentProvenance? result = null;
        if (source != null)
        {
            string sourceUri = MapSourceUri(source.SourceUri, targetLibraryId, source.DocumentId);
            result = source with
                         {
                             RevisionId = SourceDocumentRepository.MakeRevisionId(targetLibraryId,
                                                                                    targetVersion,
                                                                                    source.DocumentId),
                             SourceUri = sourceUri
                         };
        }

        return result;
    }

    private static string? MapLocalUrl(string? value,
                                       DocumentProvenance? source,
                                       DocumentProvenance? target)
    {
        string? result = value;
        if (value != null && source != null && target != null &&
            value.StartsWith(source.SourceUri, StringComparison.Ordinal))
        {
            result = $"{target.SourceUri}{value[source.SourceUri.Length..]}";
        }

        return result;
    }

    private static string MapScopedId(string sourceId,
                                      string sourceLibraryId,
                                      string sourceVersion,
                                      string targetLibraryId,
                                      string targetVersion,
                                      string opaquePrefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        string[] segments = sourceId.Split('/');
        string result;
        if (segments.Length >= CompositeIdentitySegments &&
            segments[0].Equals(sourceLibraryId, StringComparison.Ordinal) &&
            segments[1].Equals(sourceVersion, StringComparison.Ordinal))
        {
            segments[0] = targetLibraryId;
            segments[1] = targetVersion;
            result = string.Join('/', segments);
        }
        else
        {
            string identity = string.Join(UnitSeparator,
                                          targetLibraryId,
                                          targetVersion,
                                          sourceId);
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
            result = $"{opaquePrefix}-{Convert.ToHexStringLower(digest)}";
        }

        return result;
    }

    private static string? MapVersion(string? value, string sourceVersion, string targetVersion)
    {
        string? result = value;
        if (value != null && value.Equals(sourceVersion, StringComparison.Ordinal))
            result = targetVersion;
        return result;
    }

    private static string MakeSourceUri(string libraryId, string documentId) =>
        $"saddlerag://library/{libraryId}/documents/{documentId}";

    private static string MapSourceUri(string sourceUri, string targetLibraryId, string documentId) =>
        sourceUri.StartsWith(LocalSourceScheme, StringComparison.OrdinalIgnoreCase)
            ? MakeSourceUri(targetLibraryId, documentId)
            : sourceUri;

    private static void ValidateTarget(string targetLibraryId, string targetVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetLibraryId);
        ArgumentException.ThrowIfNullOrEmpty(targetVersion);
    }

    private const int CompositeIdentitySegments = 2;
    private const string PageIdPrefix = "document-page";
    private const string ChunkIdPrefix = "document-chunk";
    private const string LocalSourceScheme = "saddlerag://";
    private const char UnitSeparator = '\u001f';
}
