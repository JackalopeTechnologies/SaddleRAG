// LibraryRenameMapper.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Security.Cryptography;
using System.Text;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;
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

    internal static DirectoryLibraryDefinition MapPendingDirectoryDefinition(
        DirectoryLibraryDefinition source,
        string targetLibraryId,
        string operationId,
        string targetRegistrationIncarnationId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(targetLibraryId);
        ArgumentException.ThrowIfNullOrEmpty(operationId);
        ArgumentException.ThrowIfNullOrEmpty(targetRegistrationIncarnationId);
        var result = source with
                         {
                             Id = targetLibraryId,
                             RegistrationRevision = checked(source.RegistrationRevision + 1),
                             RegistrationIncarnationId = targetRegistrationIncarnationId,
                             PublicationLeaseScanRunId = null,
                             PublicationLeaseRegistrationRevision = null,
                             PublicationLeaseExpiresAtUtc = null,
                             PendingRenameOperationId = operationId
                         };
        return result;
    }

    internal static DirectoryLibraryDefinition MarkDirectoryDefinitionPending(
        DirectoryLibraryDefinition source,
        string operationId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(operationId);
        var result = source with { PendingRenameOperationId = operationId };
        return result;
    }

    internal static DirectoryLibraryDefinition CompleteVersionDirectoryDefinition(
        DirectoryLibraryDefinition source,
        string operationId,
        string sourceVersion,
        string targetVersion,
        string targetRegistrationIncarnationId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(operationId);
        ArgumentException.ThrowIfNullOrEmpty(targetRegistrationIncarnationId);
        var result = source with
                         {
                             LastPublishedVersion = MapVersion(source.LastPublishedVersion,
                                                               sourceVersion,
                                                               targetVersion),
                             RegistrationRevision = checked(source.RegistrationRevision + 1),
                             RegistrationIncarnationId = targetRegistrationIncarnationId,
                             PendingRenameOperationId = null,
                             PublicationLeaseScanRunId = null,
                             PublicationLeaseRegistrationRevision = null,
                             PublicationLeaseExpiresAtUtc = null
                         };
        return result;
    }

    internal static LibraryRecord MapLibrary(LibraryRecord source, string targetLibraryId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(targetLibraryId);
        return new LibraryRecord
                   {
                       Id = targetLibraryId,
                       Name = source.Name,
                       Hint = source.Hint,
                       CurrentVersion = source.CurrentVersion,
                       AllVersions = [.. source.AllVersions]
                   };
    }

    internal static LibraryRecord MapLibraryVersion(LibraryRecord source,
                                                     string sourceVersion,
                                                     string targetVersion)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(sourceVersion);
        ArgumentException.ThrowIfNullOrEmpty(targetVersion);
        return new LibraryRecord
                   {
                       Id = source.Id,
                       Name = source.Name,
                       Hint = source.Hint,
                       CurrentVersion = MapVersion(source.CurrentVersion,
                                                   sourceVersion,
                                                   targetVersion) ?? source.CurrentVersion,
                       AllVersions = ReplaceExactDistinct(source.AllVersions,
                                                          sourceVersion,
                                                          targetVersion)
                   };
    }

    internal static LibraryVersionRecord MapLibraryVersion(LibraryVersionRecord source,
                                                            string targetLibraryId,
                                                            string targetVersion,
                                                            string? sourceVersion = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateTarget(targetLibraryId, targetVersion);
        string originalVersion = sourceVersion ?? source.Version;
        var result = source with
                         {
                             Id = $"{targetLibraryId}/{targetVersion}",
                             LibraryId = targetLibraryId,
                             Version = targetVersion,
                             PreviousVersion = MapVersion(source.PreviousVersion,
                                                          originalVersion,
                                                          targetVersion)
                         };
        return result;
    }

    internal static LibraryVersionRecord MapPreviousVersionReference(LibraryVersionRecord source,
                                                                      string sourceVersion,
                                                                      string targetVersion)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = source with
                         {
                             PreviousVersion = MapVersion(source.PreviousVersion,
                                                          sourceVersion,
                                                          targetVersion)
                         };
        return result;
    }

    internal static VersionDiffRecord MapVersionDiff(VersionDiffRecord source,
                                                      string targetLibraryId,
                                                      string? sourceVersion = null,
                                                      string? targetVersion = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(targetLibraryId);
        string fromVersion = source.FromVersion;
        string toVersion = source.ToVersion;
        if (sourceVersion != null && targetVersion != null)
        {
            fromVersion = MapVersion(fromVersion, sourceVersion, targetVersion) ?? fromVersion;
            toVersion = MapVersion(toVersion, sourceVersion, targetVersion) ?? toVersion;
        }

        var result = source with
                         {
                             Id = $"{targetLibraryId}/{fromVersion}-to-{toVersion}",
                             LibraryId = targetLibraryId,
                             FromVersion = fromVersion,
                             ToVersion = toVersion
                         };
        return result;
    }

    internal static ProjectProfile MapProjectProfile(ProjectProfile source,
                                                      string sourceLibraryId,
                                                      string targetLibraryId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(sourceLibraryId);
        ArgumentException.ThrowIfNullOrEmpty(targetLibraryId);
        var result = source with
                         {
                             IngestedPackages = ReplaceExactDistinct(source.IngestedPackages,
                                                                     sourceLibraryId,
                                                                     targetLibraryId)
                         };
        return result;
    }

    internal static ScrapeAuditLogEntry MapScrapeAudit(ScrapeAuditLogEntry source,
                                                        string targetLibraryId,
                                                        string targetVersion)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateTarget(targetLibraryId, targetVersion);
        var result = source with
                         {
                             LibraryId = targetLibraryId,
                             Version = targetVersion,
                             Url = MapLocalLibraryUrl(source.Url,
                                                       source.LibraryId,
                                                       targetLibraryId) ?? source.Url,
                             ParentUrl = MapLocalLibraryUrl(source.ParentUrl,
                                                             source.LibraryId,
                                                             targetLibraryId)
                         };
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
        string sourceUri = MapSourceUri(source.SourceUri,
                                        source.LibraryId,
                                        targetLibraryId,
                                        source.Id);
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
        string sourceUri = MapSourceUri(source.SourceUri,
                                        source.LibraryId,
                                        targetLibraryId,
                                        source.Id);
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
                             Url = MapLocalUrl(source.Url, source.DocumentSource, provenance) ??
                                   MapLocalLibraryUrl(source.Url, source.LibraryId, targetLibraryId) ?? source.Url,
                             ParentUrl = MapLocalUrl(source.ParentUrl, source.DocumentSource, provenance) ??
                                         MapLocalLibraryUrl(source.ParentUrl,
                                                            source.LibraryId,
                                                            targetLibraryId),
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
                                       MapLocalLibraryUrl(source.PageUrl,
                                                          source.LibraryId,
                                                          targetLibraryId) ?? source.PageUrl,
                             ParentUrl = MapLocalUrl(source.ParentUrl, source.DocumentSource, provenance) ??
                                         MapLocalLibraryUrl(source.ParentUrl,
                                                            source.LibraryId,
                                                            targetLibraryId),
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
            string? sourceLibraryId = ExtractLocalLibraryId(source.SourceUri);
            string sourceUri = sourceLibraryId == null
                                   ? source.SourceUri
                                   : MapSourceUri(source.SourceUri,
                                                  sourceLibraryId,
                                                  targetLibraryId,
                                                  source.DocumentId);
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
        string? result = null;
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

    private static string MapSourceUri(string sourceUri,
                                       string sourceLibraryId,
                                       string targetLibraryId,
                                       string documentId) =>
        sourceUri.StartsWith(LocalLibraryPrefix(sourceLibraryId), StringComparison.OrdinalIgnoreCase)
            ? MakeSourceUri(targetLibraryId, documentId)
            : sourceUri;

    private static string? MapLocalLibraryUrl(string? value,
                                              string sourceLibraryId,
                                              string targetLibraryId)
    {
        string? result = value;
        string sourcePrefix = LocalLibraryPrefix(sourceLibraryId);
        if (value?.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) == true)
            result = $"{LocalLibraryPrefix(targetLibraryId)}{value[sourcePrefix.Length..]}";
        return result;
    }

    private static string? ExtractLocalLibraryId(string sourceUri)
    {
        string? result = null;
        if (sourceUri.StartsWith(LocalSourceScheme, StringComparison.OrdinalIgnoreCase))
        {
            int end = sourceUri.IndexOf('/', LocalSourceScheme.Length);
            if (end > LocalSourceScheme.Length)
                result = sourceUri[LocalSourceScheme.Length..end];
        }

        return result;
    }

    private static string LocalLibraryPrefix(string libraryId) => $"{LocalSourceScheme}{libraryId}/";

    private static List<string> ReplaceExactDistinct(IEnumerable<string> source,
                                                     string oldValue,
                                                     string newValue)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach(string value in source)
        {
            string mapped = value.Equals(oldValue, StringComparison.Ordinal) ? newValue : value;
            if (seen.Add(mapped))
                result.Add(mapped);
        }

        return result;
    }

    private static void ValidateTarget(string targetLibraryId, string targetVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetLibraryId);
        ArgumentException.ThrowIfNullOrEmpty(targetVersion);
    }

    private const int CompositeIdentitySegments = 2;
    private const string PageIdPrefix = "document-page";
    private const string ChunkIdPrefix = "document-chunk";
    private const string LocalSourceScheme = "saddlerag://library/";
    private const char UnitSeparator = '\u001f';
}
