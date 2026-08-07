// SourceDocumentRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Database.Repositories;

/// <summary>
///     MongoDB aggregate for local-document metadata and immutable,
///     content-addressed GridFS artifacts.
/// </summary>
public class SourceDocumentRepository : ISourceDocumentRepository
{
    public SourceDocumentRepository(SaddleRagDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        mContext = context;
    }

    private readonly SaddleRagDbContext mContext;

    /// <inheritdoc />
    public async Task UpsertDirectoryDefinitionAsync(DirectoryLibraryDefinition definition,
                                                     CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrEmpty(definition.Id);
        ArgumentNullException.ThrowIfNull(definition.RootPath);
        if (definition.BindingStatus != DirectoryLibraryBindingStatus.Unbound)
            ArgumentException.ThrowIfNullOrEmpty(definition.RootPath);

        var filter = Builders<DirectoryLibraryDefinition>.Filter.Eq(d => d.Id, definition.Id);
        await mContext.DirectoryLibraries.ReplaceOneAsync(filter,
                                                          definition,
                                                          new ReplaceOptions { IsUpsert = true },
                                                          ct);
    }

    /// <inheritdoc />
    public async Task<DirectoryLibraryDefinition> RegisterDirectoryDefinitionAsync(
        DirectoryLibraryDefinition definition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrEmpty(definition.Id);
        ArgumentException.ThrowIfNullOrEmpty(definition.RootPath);

        var filter = Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.Id, definition.Id);
        var update = Builders<DirectoryLibraryDefinition>.Update
                                                         .SetOnInsert(item => item.Id, definition.Id)
                                                         .Set(item => item.RootPath, definition.RootPath)
                                                         .Set(item => item.Name, definition.Name)
                                                         .Set(item => item.Hint, definition.Hint)
                                                         .Set(item => item.Recursive, definition.Recursive)
                                                         .Set(item => item.AllowedExtensions,
                                                              definition.AllowedExtensions)
                                                         .Set(item => item.ExclusionPatterns,
                                                              definition.ExclusionPatterns)
                                                         .Set(item => item.BindingStatus,
                                                              definition.BindingStatus)
                                                         .Set(item => item.RegisteredAtUtc,
                                                              definition.RegisteredAtUtc)
                                                         .Set(item => item.LastPublishedAtUtc, value: null)
                                                         .Set(item => item.LastPublishedVersion, value: null)
                                                         .Inc(item => item.RegistrationRevision, value: 1);
        var options = new FindOneAndUpdateOptions<DirectoryLibraryDefinition>
                          {
                              IsUpsert = true,
                              ReturnDocument = ReturnDocument.After
                          };
        DirectoryLibraryDefinition? stored = await mContext.DirectoryLibraries.FindOneAndUpdateAsync(filter,
                                                                                                      update,
                                                                                                      options,
                                                                                                      ct);
        if (stored == null)
            throw new InvalidOperationException("The directory registration was not stored.");
        DirectoryLibraryDefinition result = stored;
        return result;
    }

    /// <inheritdoc />
    public async Task<DirectoryLibraryDefinition?> GetDirectoryDefinitionAsync(string libraryId,
                                                                                CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        var result = await mContext.DirectoryLibraries.Find(d => d.Id == libraryId).FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DirectoryLibraryDefinition>> GetDirectoryDefinitionsAsync(
        CancellationToken ct = default)
    {
        var result = await mContext.DirectoryLibraries
                                   .Find(Builders<DirectoryLibraryDefinition>.Filter.Empty)
                                   .SortBy(definition => definition.Id)
                                   .ToListAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> TryUpdateDirectoryPublicationAsync(string libraryId,
                                                               long registrationRevision,
                                                               string? expectedPublishedVersion,
                                                               DateTime? publishedAtUtc,
                                                               string? publishedVersion,
                                                               CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        if (registrationRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(registrationRevision));
        if ((publishedAtUtc == null) != (publishedVersion == null))
            throw new ArgumentException("Publication time and version must both be present or both be absent.",
                                        nameof(publishedVersion));

        var filter = Builders<DirectoryLibraryDefinition>.Filter.And(
            Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.Id, libraryId),
            Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.RegistrationRevision,
                                                             registrationRevision),
            Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.LastPublishedVersion,
                                                             expectedPublishedVersion));
        var update = Builders<DirectoryLibraryDefinition>.Update
                                                         .Set(item => item.LastPublishedAtUtc, publishedAtUtc)
                                                         .Set(item => item.LastPublishedVersion, publishedVersion);
        UpdateResult updated = await mContext.DirectoryLibraries.UpdateOneAsync(filter,
                                                                                 update,
                                                                                 cancellationToken: ct);
        bool result = updated.MatchedCount == 1;
        return result;
    }

    /// <inheritdoc />
    public async Task<SourceDocumentRecord> GetOrCreateDocumentAsync(SourceDocumentRecord candidate,
                                                                     CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateDocument(candidate);

        var identityFilter = Builders<SourceDocumentRecord>.Filter.And(
            Builders<SourceDocumentRecord>.Filter.Eq(d => d.LibraryId, candidate.LibraryId),
            Builders<SourceDocumentRecord>.Filter.Eq(d => d.NormalizedRelativePath,
                                                      candidate.NormalizedRelativePath));
        var existing = await mContext.SourceDocuments.Find(identityFilter).FirstOrDefaultAsync(ct);
        SourceDocumentRecord result;
        if (existing != null)
        {
            result = existing;
        }
        else
        {
            try
            {
                await mContext.SourceDocuments.InsertOneAsync(candidate, cancellationToken: ct);
                result = candidate;
            }
            catch(MongoException ex) when (IsDuplicateKey(ex))
            {
                var winner = await mContext.SourceDocuments.Find(identityFilter).FirstOrDefaultAsync(ct);
                if (winner == null)
                    throw new InvalidOperationException("A source-document identity collision could not be resolved.", ex);
                result = winner;
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<SourceDocumentRecord?> GetDocumentAsync(string documentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        var result = await mContext.SourceDocuments.Find(d => d.Id == documentId).FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task PersistRevisionAsync(DocumentRevisionRecord revision,
                                           Stream originalArtifact,
                                           Stream? extractionArtifact,
                                           CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(originalArtifact);
        ValidateRevision(revision, originalArtifact, extractionArtifact);

        var createdHashes = new HashSet<string>(StringComparer.Ordinal);
        DocumentRevisionRecord? predecessor;
        try
        {
            var originalCreated = await EnsureArtifactAsync(revision.OriginalArtifactHash,
                                                            revision.OriginalByteLength,
                                                            originalArtifact,
                                                            ct);
            if (originalCreated)
                createdHashes.Add(revision.OriginalArtifactHash);

            if (extractionArtifact != null)
            {
                var extractionHash = revision.ExtractionArtifactHash;
                var extractionLength = revision.ExtractionByteLength;
                if (extractionHash == null || extractionLength == null)
                    throw new InvalidDataException("Validated extraction metadata is missing.");
                var extractionCreated = await EnsureArtifactAsync(extractionHash,
                                                                  extractionLength.Value,
                                                                  extractionArtifact,
                                                                  ct);
                if (extractionCreated)
                    createdHashes.Add(extractionHash);
            }

            var filter = BuildReplaceableRevisionFilter(revision.Id);
            var options = new FindOneAndReplaceOptions<DocumentRevisionRecord>
                              {
                                  IsUpsert = true,
                                  ReturnDocument = ReturnDocument.Before
                              };
            try
            {
                predecessor = await mContext.DocumentRevisions.FindOneAndReplaceAsync(filter,
                                                                                       revision,
                                                                                       options,
                                                                                       ct);
            }
            catch(MongoException ex) when (IsDuplicateKey(ex))
            {
                var current = await mContext.DocumentRevisions.Find(r => r.Id == revision.Id)
                                            .FirstOrDefaultAsync(CancellationToken.None);
                if (current?.State == DocumentRevisionState.Published)
                    throw new InvalidOperationException("A published document revision cannot be replaced.", ex);
                throw;
            }
        }
        catch
        {
            await CleanupArtifactsAsync(createdHashes, CancellationToken.None);
            throw;
        }

        if (predecessor != null)
            await CleanupSupersededArtifactsAsync(predecessor, revision, CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task<DocumentRevisionRecord?> GetRevisionAsync(string revisionId,
                                                                 CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(revisionId);
        var result = await mContext.DocumentRevisions.Find(r => r.Id == revisionId).FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentRevisionRecord>> GetRevisionsAsync(string libraryId,
                                                                                string version,
                                                                                CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        var filter = Builders<DocumentRevisionRecord>.Filter.And(
            Builders<DocumentRevisionRecord>.Filter.Eq(revision => revision.LibraryId, libraryId),
            Builders<DocumentRevisionRecord>.Filter.Eq(revision => revision.Version, version));
        var result = await mContext.DocumentRevisions.Find(filter)
                                   .SortBy(revision => revision.Id)
                                   .ToListAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentRevisionRecord>> GetRevisionsAsync(string libraryId,
                                                                                CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        var result = await mContext.DocumentRevisions.Find(revision => revision.LibraryId == libraryId)
                                   .SortBy(revision => revision.Id)
                                   .ToListAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryVersionKey>> GetDistinctLibraryVersionPairsAsync(
        CancellationToken ct = default)
    {
        var grouped = await mContext.DocumentRevisions
                                    .Aggregate()
                                    .Group(revision => new { revision.LibraryId, revision.Version },
                                           group => new { group.Key.LibraryId, group.Key.Version })
                                    .ToListAsync(ct);
        var result = grouped.Select(group => new LibraryVersionKey(group.LibraryId, group.Version)).ToList();
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetArtifactHashesBecomingUnreferencedAsync(
        IReadOnlyCollection<string> deletingRevisionIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deletingRevisionIds);
        IReadOnlyList<string> result;
        if (deletingRevisionIds.Count == 0)
        {
            result = [];
        }
        else
        {
            var deletingIds = deletingRevisionIds.ToHashSet(StringComparer.Ordinal);
            var deletingFilter = Builders<DocumentRevisionRecord>.Filter.In(revision => revision.Id,
                                                                              deletingIds);
            IReadOnlyList<DocumentRevisionRecord> deleting = await mContext.DocumentRevisions
                                                                            .Find(deletingFilter)
                                                                            .ToListAsync(ct);
            IReadOnlyList<string> candidateHashes = deleting.SelectMany(GetArtifactHashes)
                                                              .Distinct(StringComparer.Ordinal)
                                                              .ToList();
            var unreferenced = new List<string>();
            foreach(string hash in candidateHashes)
            {
                var outsideReferenceFilter = Builders<DocumentRevisionRecord>.Filter.And(
                    Builders<DocumentRevisionRecord>.Filter.Nin(revision => revision.Id, deletingIds),
                    Builders<DocumentRevisionRecord>.Filter.Or(
                        Builders<DocumentRevisionRecord>.Filter.Eq(revision => revision.OriginalArtifactHash,
                                                                    hash),
                        Builders<DocumentRevisionRecord>.Filter.Eq(revision => revision.ExtractionArtifactHash,
                                                                    hash)));
                DocumentRevisionRecord? outsideReference = await mContext.DocumentRevisions
                                                                          .Find(outsideReferenceFilter)
                                                                          .Limit(1)
                                                                          .FirstOrDefaultAsync(ct);
                if (outsideReference == null)
                    unreferenced.Add(hash);
            }

            result = unreferenced;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<Stream> OpenArtifactAsync(string sha256, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sha256);
        if (!IsCanonicalSha256(sha256))
            throw new ArgumentException("Artifact hash must be a canonical lowercase SHA-256 value.", nameof(sha256));

        var blob = await mContext.DocumentArtifactBlobs.Find(b => b.Id == sha256).FirstOrDefaultAsync(ct);
        if (blob == null)
            throw new FileNotFoundException($"Document artifact '{sha256}' was not found.");
        if (!ObjectId.TryParse(blob.GridFsId, out var fileId))
            throw new InvalidDataException($"Document artifact '{sha256}' has an invalid GridFS id.");

        Stream result;
        try
        {
            result = await mContext.DocumentArtifactsBucket.OpenDownloadStreamAsync(fileId,
                                                                                     cancellationToken: ct);
        }
        catch(GridFSFileNotFoundException ex)
        {
            throw new FileNotFoundException($"Document artifact '{sha256}' was not found.", ex);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteRevisionAsync(string revisionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(revisionId);
        var deleted = await mContext.DocumentRevisions.FindOneAndDeleteAsync(r => r.Id == revisionId,
                                                                             cancellationToken: ct);
        var result = deleted != null;
        if (deleted != null)
            await CleanupArtifactsAsync(GetArtifactHashes(deleted), CancellationToken.None);
        return result;
    }

    /// <inheritdoc />
    public async Task<long> DeleteCandidateScanRunAsync(string libraryId,
                                                        string scanRunId,
                                                        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);

        var filter = Builders<DocumentRevisionRecord>.Filter.And(
            Builders<DocumentRevisionRecord>.Filter.Eq(r => r.LibraryId, libraryId),
            Builders<DocumentRevisionRecord>.Filter.Eq(r => r.ScanRunId, scanRunId),
            Builders<DocumentRevisionRecord>.Filter.Eq(r => r.State, DocumentRevisionState.Candidate));
        var revisions = await mContext.DocumentRevisions.Find(filter).ToListAsync(ct);
        var deletion = await mContext.DocumentRevisions.DeleteManyAsync(filter, ct);
        var hashes = revisions.SelectMany(GetArtifactHashes).Distinct(StringComparer.Ordinal).ToArray();
        ExceptionDispatchInfo? failure = null;
        try
        {
            await CleanupArtifactsAsync(hashes, ct);
        }
        catch(Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        try
        {
            await DeleteUnreferencedSourceDocumentsAsync(libraryId, CurrentCleanupToken(failure, ct));
        }
        catch(Exception ex)
        {
            failure ??= ExceptionDispatchInfo.Capture(ex);
        }

        failure?.Throw();
        return deletion.DeletedCount;
    }

    /// <inheritdoc />
    public async Task<long> DeleteVersionAsync(string libraryId,
                                               string version,
                                               CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filter = Builders<DocumentRevisionRecord>.Filter.And(
            Builders<DocumentRevisionRecord>.Filter.Eq(r => r.LibraryId, libraryId),
            Builders<DocumentRevisionRecord>.Filter.Eq(r => r.Version, version));
        var revisions = await mContext.DocumentRevisions.Find(filter).ToListAsync(ct);
        var deletion = await mContext.DocumentRevisions.DeleteManyAsync(filter, ct);
        var hashes = revisions.SelectMany(GetArtifactHashes).Distinct(StringComparer.Ordinal).ToArray();
        ExceptionDispatchInfo? failure = null;
        try
        {
            await CleanupArtifactsAsync(hashes, CancellationToken.None);
        }
        catch(Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        try
        {
            await DeleteUnreferencedSourceDocumentsAsync(libraryId, CancellationToken.None);
        }
        catch(Exception ex)
        {
            failure ??= ExceptionDispatchInfo.Capture(ex);
        }

        failure?.Throw();
        return deletion.DeletedCount;
    }

    /// <inheritdoc />
    public async Task<long> DeleteLibraryAsync(string libraryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);

        var revisionFilter = Builders<DocumentRevisionRecord>.Filter.Eq(r => r.LibraryId, libraryId);
        var revisions = await mContext.DocumentRevisions.Find(revisionFilter).ToListAsync(ct);
        var deletion = await mContext.DocumentRevisions.DeleteManyAsync(revisionFilter, ct);
        var hashes = revisions.SelectMany(GetArtifactHashes).Distinct(StringComparer.Ordinal).ToArray();
        ExceptionDispatchInfo? failure = null;
        try
        {
            await mContext.SourceDocuments.DeleteManyAsync(d => d.LibraryId == libraryId,
                                                           CurrentCleanupToken(failure, ct));
        }
        catch(Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        try
        {
            await mContext.DirectoryLibraries.DeleteOneAsync(d => d.Id == libraryId,
                                                              CurrentCleanupToken(failure, ct));
        }
        catch(Exception ex)
        {
            failure ??= ExceptionDispatchInfo.Capture(ex);
        }

        try
        {
            await CleanupArtifactsAsync(hashes, CurrentCleanupToken(failure, ct));
        }
        catch(Exception ex)
        {
            failure ??= ExceptionDispatchInfo.Capture(ex);
        }

        failure?.Throw();
        return deletion.DeletedCount;
    }

    /// <inheritdoc />
    public async Task<long> SetRevisionStateAsync(string libraryId,
                                                  string version,
                                                  DocumentRevisionState state,
                                                  CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown document revision state.");

        var filter = Builders<DocumentRevisionRecord>.Filter.And(
            Builders<DocumentRevisionRecord>.Filter.Eq(r => r.LibraryId, libraryId),
            Builders<DocumentRevisionRecord>.Filter.Eq(r => r.Version, version));
        var update = Builders<DocumentRevisionRecord>.Update.Set(r => r.State, state);
        var result = await mContext.DocumentRevisions.UpdateManyAsync(filter, update, cancellationToken: ct);
        return result.ModifiedCount;
    }

    /// <inheritdoc />
    public async Task<long> PublishCandidateScanRunAsync(string libraryId,
                                                         string version,
                                                         string scanRunId,
                                                         CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);

        var filter = Builders<DocumentRevisionRecord>.Filter.And(
            Builders<DocumentRevisionRecord>.Filter.Eq(revision => revision.LibraryId, libraryId),
            Builders<DocumentRevisionRecord>.Filter.Eq(revision => revision.Version, version),
            Builders<DocumentRevisionRecord>.Filter.Eq(revision => revision.ScanRunId, scanRunId),
            Builders<DocumentRevisionRecord>.Filter.Eq(revision => revision.State,
                                                        DocumentRevisionState.Candidate));
        var update = Builders<DocumentRevisionRecord>.Update.Set(revision => revision.State,
                                                                  DocumentRevisionState.Published);
        UpdateResult result = await mContext.DocumentRevisions.UpdateManyAsync(filter,
                                                                                update,
                                                                                cancellationToken: ct);
        return result.ModifiedCount;
    }

    private async Task<bool> EnsureArtifactAsync(string sha256,
                                                 long expectedLength,
                                                 Stream content,
                                                 CancellationToken ct)
    {
        var existing = await mContext.DocumentArtifactBlobs.Find(b => b.Id == sha256).FirstOrDefaultAsync(ct);
        bool result;
        if (existing != null)
        {
            ValidateStoredArtifact(existing, expectedLength);
            await VerifyArtifactStreamAsync(sha256, expectedLength, content, ct);
            result = false;
        }
        else
        {
            using var hashingStream = new ArtifactHashingReadStream(content);
            var fileId = await mContext.DocumentArtifactsBucket.UploadFromStreamAsync(MakeArtifactFilename(sha256),
                                                                                      hashingStream,
                                                                                      cancellationToken: ct);
            try
            {
                ValidateArtifactDigest(sha256,
                                       expectedLength,
                                       hashingStream.GetSha256(),
                                       hashingStream.BytesRead);
            }
            catch
            {
                await TryDeleteGridFsFileAsync(fileId.ToString(), CancellationToken.None);
                throw;
            }
            var record = new DocumentArtifactBlobRecord
                             {
                                 Id = sha256,
                                 GridFsId = fileId.ToString(),
                                 ByteLength = expectedLength,
                                 CreatedAtUtc = DateTime.UtcNow
                             };
            try
            {
                await mContext.DocumentArtifactBlobs.InsertOneAsync(record, cancellationToken: ct);
                result = true;
            }
            catch(MongoWriteException ex) when (IsDuplicateKey(ex))
            {
                await RemoveUploadedArtifactAsync(record, CancellationToken.None);
                var winner = await mContext.DocumentArtifactBlobs.Find(b => b.Id == sha256)
                                           .FirstOrDefaultAsync(CancellationToken.None);
                if (winner == null)
                    throw new InvalidOperationException("An artifact identity collision could not be resolved.", ex);
                ValidateStoredArtifact(winner, expectedLength);
                result = false;
            }
            catch
            {
                await RemoveUploadedArtifactAsync(record, CancellationToken.None);
                throw;
            }
        }

        return result;
    }

    private static async Task VerifyArtifactStreamAsync(string expectedSha256,
                                                        long expectedLength,
                                                        Stream content,
                                                        CancellationToken ct)
    {
        using var hashingStream = new ArtifactHashingReadStream(content);
        await hashingStream.CopyToAsync(Stream.Null, ct);
        ValidateArtifactDigest(expectedSha256,
                               expectedLength,
                               hashingStream.GetSha256(),
                               hashingStream.BytesRead);
    }

    private static void ValidateArtifactDigest(string expectedSha256,
                                               long expectedLength,
                                               string actualSha256,
                                               long actualLength)
    {
        if (actualLength != expectedLength)
            throw new InvalidDataException("Artifact bytes do not match the declared byte length.");
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Artifact bytes do not match the declared SHA-256 hash.");
    }

    private async Task RemoveUploadedArtifactAsync(DocumentArtifactBlobRecord record, CancellationToken ct)
    {
        var metadataFilter = Builders<DocumentArtifactBlobRecord>.Filter.And(
            Builders<DocumentArtifactBlobRecord>.Filter.Eq(b => b.Id, record.Id),
            Builders<DocumentArtifactBlobRecord>.Filter.Eq(b => b.GridFsId, record.GridFsId));
        await mContext.DocumentArtifactBlobs.DeleteOneAsync(metadataFilter, ct);
        await TryDeleteGridFsFileAsync(record.GridFsId, ct);
    }

    private async Task CleanupSupersededArtifactsAsync(DocumentRevisionRecord previous,
                                                        DocumentRevisionRecord replacement,
                                                        CancellationToken ct)
    {
        var replacementHashes = GetArtifactHashes(replacement).ToHashSet(StringComparer.Ordinal);
        var superseded = GetArtifactHashes(previous).Where(hash => !replacementHashes.Contains(hash));
        await CleanupArtifactsAsync(superseded, ct);
    }

    private async Task CleanupArtifactsAsync(IEnumerable<string> hashes, CancellationToken ct)
    {
        ExceptionDispatchInfo? failure = null;
        foreach(var hash in hashes.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await DeleteArtifactIfUnreferencedAsync(hash, CurrentCleanupToken(failure, ct));
            }
            catch(Exception ex)
            {
                failure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        failure?.Throw();
    }

    private async Task DeleteUnreferencedSourceDocumentsAsync(string libraryId, CancellationToken ct)
    {
        var documents = await mContext.SourceDocuments.Find(d => d.LibraryId == libraryId).ToListAsync(ct);
        ExceptionDispatchInfo? failure = null;
        foreach(var document in documents)
        {
            try
            {
                var referenceFilter = Builders<DocumentRevisionRecord>.Filter.And(
                    Builders<DocumentRevisionRecord>.Filter.Eq(r => r.LibraryId, libraryId),
                    Builders<DocumentRevisionRecord>.Filter.Eq(r => r.DocumentId, document.Id));
                var reference = await mContext.DocumentRevisions.Find(referenceFilter)
                                              .Limit(1)
                                              .FirstOrDefaultAsync(CurrentCleanupToken(failure, ct));
                if (reference == null)
                {
                    var filter = Builders<SourceDocumentRecord>.Filter.And(
                        Builders<SourceDocumentRecord>.Filter.Eq(d => d.Id, document.Id),
                        Builders<SourceDocumentRecord>.Filter.Eq(d => d.LibraryId, libraryId));
                    await mContext.SourceDocuments.DeleteOneAsync(filter,
                                                                 CurrentCleanupToken(failure, ct));
                }
            }
            catch(Exception ex)
            {
                failure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        failure?.Throw();
    }

    private async Task DeleteArtifactIfUnreferencedAsync(string sha256, CancellationToken ct)
    {
        var referenceFilter = Builders<DocumentRevisionRecord>.Filter.Or(
            Builders<DocumentRevisionRecord>.Filter.Eq(r => r.OriginalArtifactHash, sha256),
            Builders<DocumentRevisionRecord>.Filter.Eq(r => r.ExtractionArtifactHash, sha256));
        var reference = await mContext.DocumentRevisions.Find(referenceFilter).Limit(1).FirstOrDefaultAsync(ct);
        if (reference == null)
        {
            var blob = await mContext.DocumentArtifactBlobs.FindOneAndDeleteAsync(b => b.Id == sha256,
                                                                                 cancellationToken: ct);
            if (blob != null)
                await TryDeleteGridFsFileAsync(blob.GridFsId, ct);
        }
    }

    private async Task TryDeleteGridFsFileAsync(string gridFsId, CancellationToken ct)
    {
        if (ObjectId.TryParse(gridFsId, out var fileId))
        {
            try
            {
                await mContext.DocumentArtifactsBucket.DeleteAsync(fileId, ct);
            }
            catch(GridFSFileNotFoundException)
            {
                // The exact file is already absent, which is the target state.
            }
        }
    }

    private static IReadOnlyList<string> GetArtifactHashes(DocumentRevisionRecord revision)
    {
        var result = new List<string> { revision.OriginalArtifactHash };
        if (revision.ExtractionArtifactHash != null)
            result.Add(revision.ExtractionArtifactHash);
        return result;
    }

    private static CancellationToken CurrentCleanupToken(ExceptionDispatchInfo? failure,
                                                          CancellationToken requestedToken)
    {
        var result = failure == null ? requestedToken : CancellationToken.None;
        return result;
    }

    private static void ValidateDocument(SourceDocumentRecord document)
    {
        ArgumentException.ThrowIfNullOrEmpty(document.Id);
        ArgumentException.ThrowIfNullOrEmpty(document.LibraryId);
        ArgumentException.ThrowIfNullOrEmpty(document.NormalizedRelativePath);
        ArgumentException.ThrowIfNullOrEmpty(document.DisplayRelativePath);
        ArgumentException.ThrowIfNullOrEmpty(document.DisplayName);
        ArgumentException.ThrowIfNullOrEmpty(document.SourceUri);
        ArgumentException.ThrowIfNullOrEmpty(document.MediaType);
        ArgumentException.ThrowIfNullOrEmpty(document.FirstSeenVersion);
    }

    private static void ValidateRevision(DocumentRevisionRecord revision,
                                         Stream originalArtifact,
                                         Stream? extractionArtifact)
    {
        ArgumentException.ThrowIfNullOrEmpty(revision.Id);
        ArgumentException.ThrowIfNullOrEmpty(revision.DocumentId);
        ArgumentException.ThrowIfNullOrEmpty(revision.LibraryId);
        ArgumentException.ThrowIfNullOrEmpty(revision.Version);
        ArgumentException.ThrowIfNullOrEmpty(revision.ScanRunId);
        ArgumentException.ThrowIfNullOrEmpty(revision.OriginalMediaType);
        ValidateArtifact(revision.OriginalArtifactHash,
                         revision.OriginalByteLength,
                         originalArtifact,
                         nameof(originalArtifact));

        var extractionMetadataPresent = revision.ExtractionArtifactHash != null ||
                                        revision.ExtractionByteLength != null ||
                                        revision.ExtractionMediaType != null ||
                                        revision.ExtractionProvenance != null;
        if (extractionArtifact == null && extractionMetadataPresent)
            throw new ArgumentException("Extraction metadata requires an extraction artifact.",
                                        nameof(extractionArtifact));
        if (extractionArtifact != null && !extractionMetadataPresent)
            throw new ArgumentException("An extraction artifact requires extraction metadata.",
                                        nameof(extractionArtifact));
        if (extractionArtifact != null)
        {
            ArgumentException.ThrowIfNullOrEmpty(revision.ExtractionMediaType);
            var extractionHash = revision.ExtractionArtifactHash;
            var extractionLength = revision.ExtractionByteLength;
            if (extractionHash == null || extractionLength == null)
                throw new ArgumentException("Extraction hash and byte length are required.",
                                            nameof(extractionArtifact));
            ValidateArtifact(extractionHash,
                             extractionLength.Value,
                             extractionArtifact,
                             nameof(extractionArtifact));
        }
    }

    private static void ValidateArtifact(string sha256, long expectedLength, Stream content, string parameterName)
    {
        if (!IsCanonicalSha256(sha256))
            throw new ArgumentException("Artifact hash must be a canonical lowercase SHA-256 value.", parameterName);
        if (expectedLength < 0)
            throw new ArgumentOutOfRangeException(parameterName, "Artifact length cannot be negative.");
        if (!content.CanRead)
            throw new ArgumentException("Artifact stream must be readable.", parameterName);
        if (content.CanSeek && content.Length - content.Position != expectedLength)
            throw new ArgumentException("Artifact stream length does not match its metadata.", parameterName);
    }

    private static void ValidateStoredArtifact(DocumentArtifactBlobRecord artifact, long expectedLength)
    {
        if (artifact.ByteLength != expectedLength)
            throw new InvalidDataException($"Artifact '{artifact.Id}' has conflicting byte-length metadata.");
    }

    private static bool IsDuplicateKey(MongoException exception)
    {
        var result = exception switch
                         {
                             MongoWriteException writeException =>
                                 writeException.WriteError?.Category == ServerErrorCategory.DuplicateKey,
                             MongoCommandException commandException => commandException.Code == DuplicateKeyErrorCode,
                             _ => false
                         };
        return result;
    }

    internal static FilterDefinition<DocumentRevisionRecord> BuildReplaceableRevisionFilter(string revisionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(revisionId);
        var result = Builders<DocumentRevisionRecord>.Filter.And(
            Builders<DocumentRevisionRecord>.Filter.Eq(r => r.Id, revisionId),
            Builders<DocumentRevisionRecord>.Filter.Ne(r => r.State, DocumentRevisionState.Published));
        return result;
    }

    public static bool IsCanonicalSha256(string? value) =>
        value is { Length: Sha256HexLength } && value.All(IsLowercaseHexCharacter);

    private static bool IsLowercaseHexCharacter(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';

    public static string MakeArtifactFilename(string sha256)
    {
        ArgumentException.ThrowIfNullOrEmpty(sha256);
        if (!IsCanonicalSha256(sha256))
            throw new ArgumentException("Artifact hash must be a canonical lowercase SHA-256 value.", nameof(sha256));
        var result = $"sha256/{sha256}";
        return result;
    }

    public static string MakeRevisionId(string libraryId, string version, string documentId)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        var composite = string.Join(UnitSeparator, libraryId, version, documentId);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(composite));
        var result = $"document-revision-{Convert.ToHexStringLower(bytes)}";
        return result;
    }

    private const int Sha256HexLength = 64;
    private const int DuplicateKeyErrorCode = 11000;
    private const char UnitSeparator = '\u001f';
}
