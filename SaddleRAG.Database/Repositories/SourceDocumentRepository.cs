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
        : this(context,
               TimeProvider.System,
               smPublicationLeaseDuration,
               smPublicationLeaseRenewalInterval)
    {
    }

    internal SourceDocumentRepository(SaddleRagDbContext context,
                                      TimeProvider timeProvider,
                                      TimeSpan publicationLeaseDuration,
                                      TimeSpan publicationLeaseRenewalInterval)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (publicationLeaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(publicationLeaseDuration));
        if (publicationLeaseRenewalInterval <= TimeSpan.Zero ||
            publicationLeaseRenewalInterval >= publicationLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(publicationLeaseRenewalInterval));
        }

        mContext = context;
        mTimeProvider = timeProvider;
        mPublicationLeaseDuration = publicationLeaseDuration;
        mPublicationLeaseRenewalInterval = publicationLeaseRenewalInterval;
    }

    private readonly SaddleRagDbContext mContext;
    private readonly TimeSpan mPublicationLeaseDuration;
    private readonly TimeSpan mPublicationLeaseRenewalInterval;
    private readonly TimeProvider mTimeProvider;

    /// <inheritdoc />
    public async Task UpsertDirectoryDefinitionAsync(DirectoryLibraryDefinition definition,
                                                     CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrEmpty(definition.Id);
        ArgumentNullException.ThrowIfNull(definition.RootPath);
        if (definition.PendingRenameOperationId != null)
            throw new ArgumentException(PendingRenameInputDetail, nameof(definition));
        if (definition.BindingStatus != DirectoryLibraryBindingStatus.Unbound)
            ArgumentException.ThrowIfNullOrEmpty(definition.RootPath);

        await ReplaceDirectoryDefinitionWhenLeaseAvailableAsync(definition, ct);
    }

    /// <inheritdoc />
    public async Task<DirectoryLibraryDefinition> RegisterDirectoryDefinitionAsync(
        DirectoryLibraryDefinition definition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrEmpty(definition.Id);
        ArgumentException.ThrowIfNullOrEmpty(definition.RootPath);
        if (definition.PendingRenameOperationId != null)
            throw new ArgumentException(PendingRenameInputDetail, nameof(definition));

        DirectoryLibraryDefinition result = await RegisterDirectoryDefinitionWhenLeaseAvailableAsync(definition,
                                                                                                        ct);
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
    public async Task<IDirectoryPublicationLease?> TryAcquireDirectoryPublicationLeaseAsync(
        string libraryId,
        long registrationRevision,
        string? registrationIncarnationId,
        string scanRunId,
        string? expectedPublishedVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        if (registrationRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(registrationRevision));
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);

        DateTime acquiredAtUtc = mTimeProvider.GetUtcNow().UtcDateTime;
        DateTime expiresAtUtc = acquiredAtUtc.Add(mPublicationLeaseDuration);
        FilterDefinition<DirectoryLibraryDefinition> filter =
            Builders<DirectoryLibraryDefinition>.Filter.And(
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.Id, libraryId),
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.RegistrationRevision,
                                                                 registrationRevision),
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.RegistrationIncarnationId,
                                                                 registrationIncarnationId),
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.LastPublishedVersion,
                                                                 expectedPublishedVersion),
                NormalLifecycleFilter(),
                AvailablePublicationLeaseFilter(acquiredAtUtc));
        UpdateDefinition<DirectoryLibraryDefinition> update =
            Builders<DirectoryLibraryDefinition>.Update
                                                .Set(item => item.PublicationLeaseScanRunId, scanRunId)
                                                .Set(item => item.PublicationLeaseRegistrationRevision,
                                                     registrationRevision)
                                                .Set(item => item.PublicationLeaseExpiresAtUtc, expiresAtUtc);
        var options = new FindOneAndUpdateOptions<DirectoryLibraryDefinition>
                          {
                              ReturnDocument = ReturnDocument.After
                          };
        DirectoryLibraryDefinition? leased = await mContext.DirectoryLibraries.FindOneAndUpdateAsync(filter,
                                                                                                       update,
                                                                                                       options,
                                                                                                       ct);
        IDirectoryPublicationLease? result = leased == null
                                                 ? null
                                                 : new MongoDirectoryPublicationLease(this,
                                                     libraryId,
                                                     scanRunId,
                                                     registrationIncarnationId,
                                                     registrationRevision);
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> TryUpdateDirectoryPublicationAsync(IDirectoryPublicationLease lease,
                                                               string? expectedPublishedVersion,
                                                               DateTime? publishedAtUtc,
                                                               string? publishedVersion,
                                                               CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLease(lease);
        if ((publishedAtUtc == null) != (publishedVersion == null))
            throw new ArgumentException("Publication time and version must both be present or both be absent.",
                                        nameof(publishedVersion));

        DateTime updatedAtUtc = mTimeProvider.GetUtcNow().UtcDateTime;
        var filter = Builders<DirectoryLibraryDefinition>.Filter.And(
            Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.Id, lease.LibraryId),
            Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.RegistrationRevision,
                                                             lease.RegistrationRevision),
            Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.RegistrationIncarnationId,
                                                             lease.RegistrationIncarnationId),
            Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.PublicationLeaseScanRunId,
                                                             lease.ScanRunId),
            Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.PublicationLeaseRegistrationRevision,
                                                             lease.RegistrationRevision),
            Builders<DirectoryLibraryDefinition>.Filter.Gt(item => item.PublicationLeaseExpiresAtUtc,
                                                             updatedAtUtc),
            NormalLifecycleFilter(),
            Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.LastPublishedVersion,
                                                             expectedPublishedVersion));
        var update = Builders<DirectoryLibraryDefinition>.Update
                                                         .Set(item => item.LastPublishedAtUtc, publishedAtUtc)
                                                         .Set(item => item.LastPublishedVersion, publishedVersion)
                                                         .Set(item => item.PublicationLeaseExpiresAtUtc,
                                                              updatedAtUtc.Add(mPublicationLeaseDuration));
        UpdateResult updated = await mContext.DirectoryLibraries.UpdateOneAsync(filter,
                                                                                 update,
                                                                                 cancellationToken: ct);
        bool result = updated.MatchedCount == 1;
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> TryApplyDirectoryPackagePublicationAsync(
        IDirectoryPublicationLease lease,
        string? expectedPublishedVersion,
        DirectoryLibraryDefinition packageDefinition,
        DateTime publishedAtUtc,
        string publishedVersion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLease(lease);
        ArgumentNullException.ThrowIfNull(packageDefinition);
        ArgumentException.ThrowIfNullOrEmpty(publishedVersion);
        if (!string.Equals(lease.LibraryId, packageDefinition.Id, StringComparison.Ordinal))
            throw new ArgumentException("The package definition belongs to a different library.",
                                        nameof(packageDefinition));
        if (packageDefinition.PendingRenameOperationId != null)
            throw new ArgumentException(PendingRenameInputDetail, nameof(packageDefinition));

        DateTime updatedAtUtc = mTimeProvider.GetUtcNow().UtcDateTime;
        FilterDefinition<DirectoryLibraryDefinition> filter =
            Builders<DirectoryLibraryDefinition>.Filter.And(
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.Id, lease.LibraryId),
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.RegistrationRevision,
                                                                 lease.RegistrationRevision),
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.RegistrationIncarnationId,
                                                                 lease.RegistrationIncarnationId),
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.PublicationLeaseScanRunId,
                                                                 lease.ScanRunId),
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.PublicationLeaseRegistrationRevision,
                                                                 lease.RegistrationRevision),
                Builders<DirectoryLibraryDefinition>.Filter.Gt(item => item.PublicationLeaseExpiresAtUtc,
                                                                 updatedAtUtc),
                NormalLifecycleFilter(),
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.LastPublishedVersion,
                                                                 expectedPublishedVersion));
        UpdateDefinition<DirectoryLibraryDefinition> update =
            Builders<DirectoryLibraryDefinition>.Update
                                                .Set(item => item.Name, packageDefinition.Name)
                                                .Set(item => item.Hint, packageDefinition.Hint)
                                                .Set(item => item.Recursive, packageDefinition.Recursive)
                                                .Set(item => item.AllowedExtensions,
                                                     packageDefinition.AllowedExtensions)
                                                .Set(item => item.ExclusionPatterns,
                                                     packageDefinition.ExclusionPatterns)
                                                .Set(item => item.LastPublishedAtUtc, publishedAtUtc)
                                                .Set(item => item.LastPublishedVersion, publishedVersion)
                                                .Set(item => item.PublicationLeaseExpiresAtUtc,
                                                     updatedAtUtc.Add(mPublicationLeaseDuration));
        UpdateResult updated = await mContext.DirectoryLibraries.UpdateOneAsync(filter,
                                                                                 update,
                                                                                 cancellationToken: ct);
        return updated.MatchedCount == 1;
    }

    /// <inheritdoc />
    public async Task<bool> TryRestoreDirectoryPublicationAsync(IDirectoryPublicationLease lease,
                                                                string failedPublishedVersion,
                                                                DateTime? restoredPublishedAtUtc,
                                                                string? restoredPublishedVersion,
                                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLease(lease);
        ArgumentException.ThrowIfNullOrEmpty(failedPublishedVersion);
        if ((restoredPublishedAtUtc == null) != (restoredPublishedVersion == null))
            throw new ArgumentException("Publication time and version must both be present or both be absent.",
                                        nameof(restoredPublishedVersion));

        DateTime restoredAtUtc = mTimeProvider.GetUtcNow().UtcDateTime;
        FilterDefinition<DirectoryLibraryDefinition> filter =
            Builders<DirectoryLibraryDefinition>.Filter.And(
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.Id, lease.LibraryId),
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.RegistrationRevision,
                                                                 lease.RegistrationRevision),
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.RegistrationIncarnationId,
                                                                 lease.RegistrationIncarnationId),
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.PublicationLeaseScanRunId,
                                                                 lease.ScanRunId),
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.PublicationLeaseRegistrationRevision,
                                                                 lease.RegistrationRevision),
                Builders<DirectoryLibraryDefinition>.Filter.Gt(item => item.PublicationLeaseExpiresAtUtc,
                                                                 restoredAtUtc),
                NormalLifecycleFilter(),
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.LastPublishedVersion,
                                                                 failedPublishedVersion));
        UpdateDefinition<DirectoryLibraryDefinition> update =
            Builders<DirectoryLibraryDefinition>.Update
                                                .Set(item => item.LastPublishedAtUtc, restoredPublishedAtUtc)
                                                .Set(item => item.LastPublishedVersion, restoredPublishedVersion)
                                                .Set(item => item.PublicationLeaseExpiresAtUtc,
                                                     restoredAtUtc.Add(mPublicationLeaseDuration));
        UpdateResult restored = await mContext.DirectoryLibraries.UpdateOneAsync(filter,
                                                                                  update,
                                                                                  cancellationToken: ct);
        bool result = restored.MatchedCount == 1;
        if (!result)
        {
            DirectoryLibraryDefinition? current = await mContext.DirectoryLibraries
                                                                 .Find(item => item.Id == lease.LibraryId)
                                                                 .FirstOrDefaultAsync(ct);
            result = current == null ||
                     !failedPublishedVersion.Equals(current.LastPublishedVersion, StringComparison.Ordinal);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> TryDeleteLeasedDirectoryDefinitionAsync(IDirectoryPublicationLease lease,
                                                                     CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLease(lease);
        if (lease is not MongoDirectoryPublicationLease mongoLease)
            throw new ArgumentException("The lease was not created by this repository implementation.",
                                        nameof(lease));

        bool result = await mongoLease.TryDeleteDefinitionAsync(ct);
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

        string operationId = Guid.NewGuid().ToString("N");
        DateTime preparedAtUtc = DateTime.UtcNow;
        var preparedClaims = new List<DocumentRevisionArtifactClaim>();
        DocumentRevisionRecord? predecessor;
        try
        {
            DocumentRevisionArtifactClaim? originalClaim = await PrepareArtifactClaimAsync(
                                                                       revision.OriginalArtifactHash,
                                                                       revision.OriginalByteLength,
                                                                       originalArtifact,
                                                                       revision.Id,
                                                                       operationId,
                                                                       preparedAtUtc,
                                                                       streamAlreadyVerified: false,
                                                                       ct);
            if (originalClaim != null)
                preparedClaims.Add(originalClaim);

            if (extractionArtifact != null)
            {
                var extractionHash = revision.ExtractionArtifactHash;
                var extractionLength = revision.ExtractionByteLength;
                if (extractionHash == null || extractionLength == null)
                    throw new InvalidDataException("Validated extraction metadata is missing.");
                DocumentRevisionArtifactClaim? extractionClaim;
                if (string.Equals(extractionHash, revision.OriginalArtifactHash, StringComparison.Ordinal))
                {
                    await VerifyArtifactStreamAsync(extractionHash,
                                                    extractionLength.Value,
                                                    extractionArtifact,
                                                    ct);
                    extractionClaim = originalClaim;
                }
                else
                {
                    extractionClaim = await PrepareArtifactClaimAsync(extractionHash,
                                                                       extractionLength.Value,
                                                                       extractionArtifact,
                                                                       revision.Id,
                                                                       operationId,
                                                                       preparedAtUtc,
                                                                       streamAlreadyVerified: false,
                                                                       ct);
                }

                if (extractionClaim != null &&
                    preparedClaims.All(claim => !string.Equals(claim.ArtifactHash,
                                                                extractionClaim.ArtifactHash,
                                                                StringComparison.Ordinal)))
                {
                    preparedClaims.Add(extractionClaim);
                }
            }

            await RenewPreparedClaimsAsync(revision.Id, preparedClaims, ct);
            DocumentRevisionRecord claimedRevision = revision with
                                                         {
                                                             ArtifactClaims = preparedClaims
                                                                 .OrderBy(claim => claim.ArtifactHash,
                                                                          StringComparer.Ordinal)
                                                                 .ToList()
                                                         };
            var filter = BuildReplaceableRevisionFilter(revision.Id);
            var options = new FindOneAndReplaceOptions<DocumentRevisionRecord>
                              {
                                  IsUpsert = true,
                                  ReturnDocument = ReturnDocument.Before
                              };
            try
            {
                predecessor = await mContext.DocumentRevisions.FindOneAndReplaceAsync(filter,
                                                                                       claimedRevision,
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
            await ReleaseArtifactClaimsAsync(revision.Id, preparedClaims, CancellationToken.None);
            throw;
        }

        await FinalizeArtifactClaimsAsync(revision.Id, preparedClaims, CancellationToken.None);
        if (predecessor != null)
            await ReleaseArtifactClaimsAsync(predecessor.Id,
                                             predecessor.ArtifactClaims,
                                             CancellationToken.None);
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
        if (!string.IsNullOrEmpty(blob.DeletionId))
            throw new FileNotFoundException($"Document artifact '{sha256}' is being deleted.");
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
            await ReleaseArtifactClaimsAsync(deleted.Id,
                                             deleted.ArtifactClaims,
                                             CancellationToken.None);
        return result;
    }

    /// <inheritdoc />
    public async Task<DocumentArtifactRecoveryResult> RecoverArtifactClaimsAsync(
        DateTime utcNow,
        CancellationToken ct = default)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Artifact recovery time must use DateTimeKind.Utc.", nameof(utcNow));

        var managedFilter = Builders<DocumentArtifactBlobRecord>.Filter.Eq(
            artifact => artifact.ClaimSchemaVersion,
            CurrentArtifactClaimSchemaVersion);
        var counts = ArtifactRecoveryCounts.Empty;
        using IAsyncCursor<DocumentArtifactBlobRecord> artifacts = await mContext.DocumentArtifactBlobs
                                                                                  .Find(managedFilter)
                                                                                  .SortBy(artifact => artifact.Id)
                                                                                  .ToCursorAsync(ct);
        while(await artifacts.MoveNextAsync(ct))
        {
            foreach(DocumentArtifactBlobRecord artifact in artifacts.Current)
                counts = counts.Add(await RecoverArtifactAsync(artifact, utcNow, ct));
        }

        return new DocumentArtifactRecoveryResult(counts.ClaimsFinalized,
                                                  counts.ClaimsReleased,
                                                  counts.ArtifactDeletionsCompleted);
    }

    private async Task<ArtifactRecoveryCounts> RecoverArtifactAsync(DocumentArtifactBlobRecord artifact,
                                                                     DateTime utcNow,
                                                                     CancellationToken ct)
    {
        ArtifactRecoveryCounts result;
        if (!string.IsNullOrEmpty(artifact.DeletionId))
        {
            bool deleted = await CompleteArtifactDeletionAsync(artifact, ct);
            result = ArtifactRecoveryCounts.Empty with
                         {
                             ArtifactDeletionsCompleted = deleted ? 1 : 0
                         };
        }
        else
        {
            result = ArtifactRecoveryCounts.Empty;
            foreach(DocumentArtifactClaimRecord claim in artifact.Claims)
                result = result.Add(await RecoverArtifactClaimAsync(artifact.Id, claim, utcNow, ct));
            bool deleted = await TryDeleteManagedArtifactAsync(artifact.Id, ct);
            result = result with
                         {
                             ArtifactDeletionsCompleted = result.ArtifactDeletionsCompleted +
                                                          (deleted ? 1 : 0)
                         };
        }

        return result;
    }

    private async Task<ArtifactRecoveryCounts> RecoverArtifactClaimAsync(
        string artifactHash,
        DocumentArtifactClaimRecord claim,
        DateTime utcNow,
        CancellationToken ct)
    {
        var result = ArtifactRecoveryCounts.Empty;
        if (IsWellFormedClaim(claim))
        {
            DocumentRevisionRecord? revision = await mContext.DocumentRevisions
                                                              .Find(item => item.Id == claim.RevisionId)
                                                              .FirstOrDefaultAsync(ct);
            bool committed = revision?.ArtifactClaims.Any(item =>
                                 string.Equals(item.ArtifactHash, artifactHash, StringComparison.Ordinal) &&
                                 string.Equals(item.ClaimId, claim.ClaimId, StringComparison.Ordinal)) == true;
            bool shouldFinalize = committed && claim.FinalizedAtUtc == null;
            bool shouldRelease = !committed &&
                                 (claim.FinalizedAtUtc != null || claim.ExpiresAtUtc <= utcNow);
            if (shouldFinalize && await TryFinalizeArtifactClaimAsync(artifactHash,
                                                                       claim.RevisionId,
                                                                       claim.ClaimId,
                                                                       utcNow,
                                                                       ct))
            {
                result = result with { ClaimsFinalized = 1 };
            }
            if (shouldRelease && await TryReleaseObservedArtifactClaimAsync(artifactHash,
                                                                             claim,
                                                                             ct))
            {
                result = result with { ClaimsReleased = 1 };
            }
        }

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
        long deletionCount = await DeleteRevisionsAsync(filter, ct);
        ExceptionDispatchInfo? failure = null;
        try
        {
            await DeleteUnreferencedSourceDocumentsAsync(libraryId, CurrentCleanupToken(failure, ct));
        }
        catch(Exception ex)
        {
            failure ??= ExceptionDispatchInfo.Capture(ex);
        }

        failure?.Throw();
        return deletionCount;
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
        long deletionCount = await DeleteRevisionsAsync(filter, ct);
        ExceptionDispatchInfo? failure = null;
        try
        {
            await DeleteUnreferencedSourceDocumentsAsync(libraryId, CancellationToken.None);
        }
        catch(Exception ex)
        {
            failure ??= ExceptionDispatchInfo.Capture(ex);
        }

        failure?.Throw();
        return deletionCount;
    }

    /// <inheritdoc />
    public async Task<long> DeleteLibraryAsync(string libraryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);

        var revisionFilter = Builders<DocumentRevisionRecord>.Filter.Eq(r => r.LibraryId, libraryId);
        long deletionCount = await DeleteRevisionsAsync(revisionFilter, ct);
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

        failure?.Throw();
        return deletionCount;
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
        DateTime publishedAtUtc = mTimeProvider.GetUtcNow().UtcDateTime;
        var update = Builders<DocumentRevisionRecord>.Update
            .Set(revision => revision.State, DocumentRevisionState.Published)
            .Set(revision => revision.PublishedAtUtc, publishedAtUtc);
        UpdateResult result = await mContext.DocumentRevisions.UpdateManyAsync(filter,
                                                                                update,
                                                                                cancellationToken: ct);
        var promotedFilter = Builders<DocumentRevisionRecord>.Filter.And(
            Builders<DocumentRevisionRecord>.Filter.Eq(revision => revision.LibraryId, libraryId),
            Builders<DocumentRevisionRecord>.Filter.Eq(revision => revision.Version, version),
            Builders<DocumentRevisionRecord>.Filter.Eq(revision => revision.ScanRunId, scanRunId),
            Builders<DocumentRevisionRecord>.Filter.Eq(revision => revision.State,
                                                        DocumentRevisionState.Published));
        List<string> documentIds = (await mContext.DocumentRevisions.Find(promotedFilter)
                                                                  .Project(revision => revision.DocumentId)
                                                                  .ToListAsync(ct))
                                  .Distinct(StringComparer.Ordinal)
                                  .ToList();
        if (documentIds.Count > 0)
        {
            var sourceFilter = Builders<SourceDocumentRecord>.Filter.And(
                Builders<SourceDocumentRecord>.Filter.Eq(document => document.LibraryId, libraryId),
                Builders<SourceDocumentRecord>.Filter.In(document => document.Id, documentIds));
            var sourceUpdate = Builders<SourceDocumentRecord>.Update
                .Set(document => document.LastSeenVersion, version)
                .Set(document => document.UpdatedAtUtc, publishedAtUtc);
            UpdateResult sourceResult = await mContext.SourceDocuments.UpdateManyAsync(sourceFilter,
                                                                                         sourceUpdate,
                                                                                         cancellationToken: ct);
            if (sourceResult.MatchedCount != documentIds.Count)
            {
                throw new InvalidOperationException(
                    "Every published document revision must retain its source-document identity.");
            }
        }

        return result.ModifiedCount;
    }

    private async Task<DocumentRevisionArtifactClaim?> PrepareArtifactClaimAsync(
        string sha256,
        long expectedLength,
        Stream content,
        string revisionId,
        string operationId,
        DateTime preparedAtUtc,
        bool streamAlreadyVerified,
        CancellationToken ct)
    {
        string claimId = $"{operationId}:{sha256}";
        var claim = new DocumentArtifactClaimRecord
                        {
                            ClaimId = claimId,
                            RevisionId = revisionId,
                            PreparedAtUtc = preparedAtUtc,
                            ExpiresAtUtc = preparedAtUtc.Add(smArtifactClaimPreparationLease),
                            FinalizedAtUtc = null
                        };
        DocumentArtifactBlobRecord? existing = await mContext.DocumentArtifactBlobs
                                                                     .Find(artifact => artifact.Id == sha256)
                                                                     .FirstOrDefaultAsync(ct);
        DocumentRevisionArtifactClaim? result;
        if (existing == null)
        {
            result = await UploadManagedArtifactAsync(sha256,
                                                      expectedLength,
                                                      content,
                                                      claim,
                                                      ct);
        }
        else
        {
            result = await ClaimExistingArtifactAsync(existing,
                                                      expectedLength,
                                                      content,
                                                      claim,
                                                      streamAlreadyVerified,
                                                      ct);
        }

        return result;
    }

    private async Task<DocumentRevisionArtifactClaim?> UploadManagedArtifactAsync(
        string sha256,
        long expectedLength,
        Stream content,
        DocumentArtifactClaimRecord claim,
        CancellationToken ct)
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
                             CreatedAtUtc = DateTime.UtcNow,
                             ClaimSchemaVersion = CurrentArtifactClaimSchemaVersion,
                             Claims = [claim],
                             DeletionId = null,
                             DeletionPreparedAtUtc = null
                         };
        DocumentRevisionArtifactClaim? result;
        try
        {
            await mContext.DocumentArtifactBlobs.InsertOneAsync(record, cancellationToken: ct);
            result = new DocumentRevisionArtifactClaim { ArtifactHash = sha256, ClaimId = claim.ClaimId };
        }
        catch(MongoWriteException ex) when (IsDuplicateKey(ex))
        {
            await TryDeleteGridFsFileAsync(record.GridFsId, CancellationToken.None);
            DocumentArtifactBlobRecord? winner = await mContext.DocumentArtifactBlobs
                                                                 .Find(artifact => artifact.Id == sha256)
                                                                 .FirstOrDefaultAsync(CancellationToken.None);
            if (winner == null)
                throw new InvalidOperationException("An artifact identity collision could not be resolved.", ex);
            result = await ClaimExistingArtifactAsync(winner,
                                                      expectedLength,
                                                      content,
                                                      claim,
                                                      streamAlreadyVerified: true,
                                                      CancellationToken.None);
        }
        catch
        {
            await ReconcileAmbiguousUploadAsync(record, CancellationToken.None);
            throw;
        }

        return result;
    }

    private async Task<DocumentRevisionArtifactClaim?> ClaimExistingArtifactAsync(
        DocumentArtifactBlobRecord existing,
        long expectedLength,
        Stream content,
        DocumentArtifactClaimRecord claim,
        bool streamAlreadyVerified,
        CancellationToken ct)
    {
        ValidateStoredArtifact(existing, expectedLength);
        DocumentRevisionArtifactClaim? result;
        switch(existing.ClaimSchemaVersion)
        {
            case null:
                if (!streamAlreadyVerified)
                    await VerifyArtifactStreamAsync(existing.Id, expectedLength, content, ct);
                result = null;
                break;
            case CurrentArtifactClaimSchemaVersion:
                result = await ClaimManagedExistingArtifactAsync(existing,
                                                                 expectedLength,
                                                                 content,
                                                                 claim,
                                                                 streamAlreadyVerified,
                                                                 ct);
                break;
            default:
                throw new InvalidDataException(
                    $"Artifact '{existing.Id}' uses an unsupported ownership schema and cannot accept a new claim.");
        }

        return result;
    }

    private async Task<DocumentRevisionArtifactClaim> ClaimManagedExistingArtifactAsync(
        DocumentArtifactBlobRecord existing,
        long expectedLength,
        Stream content,
        DocumentArtifactClaimRecord claim,
        bool streamAlreadyVerified,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(existing.DeletionId))
        {
            await CompleteArtifactDeletionAsync(existing, ct);
            throw new InvalidOperationException(
                $"Artifact '{existing.Id}' deletion won the claim race; retry revision persistence.");
        }
        if (existing.Claims.Count >= MaximumArtifactClaimsPerBlob)
        {
            throw new InvalidOperationException(
                $"Artifact '{existing.Id}' reached the managed ownership-claim limit.");
        }

        bool claimed = await TryAddPreparedArtifactClaimAsync(existing.Id,
                                                               existing.Claims.Count,
                                                               claim,
                                                               ct);
        if (!claimed)
            await RequirePreparedArtifactClaimAsync(existing.Id, claim, ct);
        await VerifyClaimedArtifactStreamAsync(existing.Id,
                                               expectedLength,
                                               content,
                                               claim,
                                               streamAlreadyVerified,
                                               ct);
        return new DocumentRevisionArtifactClaim
                   {
                       ArtifactHash = existing.Id,
                       ClaimId = claim.ClaimId
                   };
    }

    private async Task RequirePreparedArtifactClaimAsync(string artifactHash,
                                                         DocumentArtifactClaimRecord claim,
                                                         CancellationToken ct)
    {
        DocumentArtifactBlobRecord? current = await mContext.DocumentArtifactBlobs
                                                            .Find(artifact => artifact.Id == artifactHash)
                                                            .FirstOrDefaultAsync(ct);
        bool exactClaimExists = current?.ClaimSchemaVersion == CurrentArtifactClaimSchemaVersion &&
                                string.IsNullOrEmpty(current.DeletionId) &&
                                current.Claims.Any(candidate =>
                                    string.Equals(candidate.ClaimId, claim.ClaimId, StringComparison.Ordinal) &&
                                    string.Equals(candidate.RevisionId,
                                                  claim.RevisionId,
                                                  StringComparison.Ordinal));
        if (!exactClaimExists)
        {
            throw new InvalidOperationException(
                $"Artifact '{artifactHash}' changed while its ownership claim was prepared; retry revision persistence.");
        }
    }

    private async Task VerifyClaimedArtifactStreamAsync(string artifactHash,
                                                        long expectedLength,
                                                        Stream content,
                                                        DocumentArtifactClaimRecord claim,
                                                        bool streamAlreadyVerified,
                                                        CancellationToken ct)
    {
        try
        {
            if (!streamAlreadyVerified)
                await VerifyArtifactStreamAsync(artifactHash, expectedLength, content, ct);
        }
        catch
        {
            await TryReleaseArtifactClaimAsync(artifactHash,
                                               claim.RevisionId,
                                               claim.ClaimId,
                                               CancellationToken.None);
            await TryDeleteManagedArtifactAsync(artifactHash, CancellationToken.None);
            throw;
        }
    }

    private async Task<bool> TryAddPreparedArtifactClaimAsync(string artifactHash,
                                                               int expectedClaimCount,
                                                               DocumentArtifactClaimRecord claim,
                                                               CancellationToken ct)
    {
        FilterDefinition<DocumentArtifactBlobRecord> exactClaim =
            Builders<DocumentArtifactBlobRecord>.Filter.ElemMatch(
                artifact => artifact.Claims,
                candidate => candidate.ClaimId == claim.ClaimId);
        FilterDefinition<DocumentArtifactBlobRecord> filter =
            Builders<DocumentArtifactBlobRecord>.Filter.And(
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.Id, artifactHash),
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.ClaimSchemaVersion,
                                                                 CurrentArtifactClaimSchemaVersion),
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.DeletionId, value: null),
                Builders<DocumentArtifactBlobRecord>.Filter.Size(ArtifactClaimsFieldName,
                                                                  expectedClaimCount),
                Builders<DocumentArtifactBlobRecord>.Filter.Not(exactClaim));
        UpdateDefinition<DocumentArtifactBlobRecord> update =
            Builders<DocumentArtifactBlobRecord>.Update.Push(artifact => artifact.Claims, claim);
        UpdateResult added = await mContext.DocumentArtifactBlobs.UpdateOneAsync(filter,
                                                                                  update,
                                                                                  cancellationToken: ct);
        return added.ModifiedCount == 1;
    }

    private async Task RenewPreparedClaimsAsync(string revisionId,
                                                IReadOnlyList<DocumentRevisionArtifactClaim> claims,
                                                CancellationToken ct)
    {
        DateTime renewalAtUtc = DateTime.UtcNow;
        DateTime expiresAtUtc = renewalAtUtc.Add(smArtifactClaimPreparationLease);
        foreach(DocumentRevisionArtifactClaim claim in claims)
        {
            bool renewed = await TryRenewPreparedArtifactClaimAsync(claim.ArtifactHash,
                                                                     revisionId,
                                                                     claim.ClaimId,
                                                                     renewalAtUtc,
                                                                     expiresAtUtc,
                                                                     ct);
            if (!renewed)
            {
                throw new InvalidOperationException(
                    $"Artifact '{claim.ArtifactHash}' ownership claim was lost before revision persistence.");
            }
        }
    }

    internal async Task<bool> TryRenewPreparedArtifactClaimAsync(string artifactHash,
                                                                  string revisionId,
                                                                  string claimId,
                                                                  DateTime renewalAtUtc,
                                                                  DateTime expiresAtUtc,
                                                                  CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactHash);
        ArgumentException.ThrowIfNullOrEmpty(revisionId);
        ArgumentException.ThrowIfNullOrEmpty(claimId);
        if (renewalAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Artifact claim renewal time must use DateTimeKind.Utc.",
                                        nameof(renewalAtUtc));
        if (expiresAtUtc.Kind != DateTimeKind.Utc || expiresAtUtc <= renewalAtUtc)
        {
            throw new ArgumentException(
                "Artifact claim expiration must be a UTC time after the renewal time.",
                nameof(expiresAtUtc));
        }

        FilterDefinition<DocumentArtifactClaimRecord> renewableClaim =
            Builders<DocumentArtifactClaimRecord>.Filter.And(
                Builders<DocumentArtifactClaimRecord>.Filter.Eq(claim => claim.ClaimId, claimId),
                Builders<DocumentArtifactClaimRecord>.Filter.Eq(claim => claim.RevisionId, revisionId),
                Builders<DocumentArtifactClaimRecord>.Filter.Eq(claim => claim.FinalizedAtUtc, value: null),
                Builders<DocumentArtifactClaimRecord>.Filter.Gt(claim => claim.ExpiresAtUtc, renewalAtUtc));
        FilterDefinition<DocumentArtifactBlobRecord> filter =
            Builders<DocumentArtifactBlobRecord>.Filter.And(
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.Id, artifactHash),
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.ClaimSchemaVersion,
                                                                 CurrentArtifactClaimSchemaVersion),
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.DeletionId, value: null),
                Builders<DocumentArtifactBlobRecord>.Filter.ElemMatch(artifact => artifact.Claims,
                                                                       renewableClaim));
        UpdateDefinition<DocumentArtifactBlobRecord> update =
            Builders<DocumentArtifactBlobRecord>.Update.Set(ArtifactClaimExpiresAtFieldPath, expiresAtUtc);
        UpdateResult renewed = await mContext.DocumentArtifactBlobs.UpdateOneAsync(filter,
                                                                                    update,
                                                                                    cancellationToken: ct);
        return renewed.MatchedCount == 1;
    }

    private async Task FinalizeArtifactClaimsAsync(string revisionId,
                                                   IReadOnlyList<DocumentRevisionArtifactClaim> claims,
                                                   CancellationToken ct)
    {
        DateTime finalizedAtUtc = DateTime.UtcNow;
        foreach(DocumentRevisionArtifactClaim claim in claims)
        {
            bool finalized = await TryFinalizeArtifactClaimAsync(claim.ArtifactHash,
                                                                  revisionId,
                                                                  claim.ClaimId,
                                                                  finalizedAtUtc,
                                                                  ct);
            if (!finalized)
            {
                DocumentRevisionRecord? current = await mContext.DocumentRevisions
                                                                  .Find(revision => revision.Id == revisionId)
                                                                  .FirstOrDefaultAsync(ct);
                bool stillCommitted = current?.ArtifactClaims.Any(candidate =>
                                          string.Equals(candidate.ArtifactHash,
                                                        claim.ArtifactHash,
                                                        StringComparison.Ordinal) &&
                                          string.Equals(candidate.ClaimId,
                                                        claim.ClaimId,
                                                        StringComparison.Ordinal)) == true;
                if (stillCommitted)
                {
                    throw new InvalidOperationException(
                        $"Committed artifact '{claim.ArtifactHash}' ownership claim could not be finalized.");
                }
            }
        }
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

    internal async Task ReconcileAmbiguousUploadAsync(DocumentArtifactBlobRecord record,
                                                       CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(record.Id);
        ArgumentException.ThrowIfNullOrEmpty(record.GridFsId);
        DocumentArtifactBlobRecord? current = await mContext.DocumentArtifactBlobs
                                                            .Find(artifact => artifact.Id == record.Id)
                                                            .FirstOrDefaultAsync(ct);
        // When metadata is not visible, retaining an unreferenced upload is
        // safer than deleting bytes whose metadata may still become visible.
        bool sameUpload = current != null &&
                          string.Equals(current.GridFsId, record.GridFsId, StringComparison.Ordinal);
        if (current != null && !sameUpload)
        {
            await TryDeleteGridFsFileAsync(record.GridFsId, ct);
        }
        if (current is { ClaimSchemaVersion: CurrentArtifactClaimSchemaVersion } &&
            sameUpload &&
            record.Claims.Count == 1)
        {
            DocumentArtifactClaimRecord claim = record.Claims[index: 0];
            FilterDefinition<DocumentArtifactClaimRecord> exactClaim =
                Builders<DocumentArtifactClaimRecord>.Filter.And(
                    Builders<DocumentArtifactClaimRecord>.Filter.Eq(candidate => candidate.ClaimId,
                                                                      claim.ClaimId),
                    Builders<DocumentArtifactClaimRecord>.Filter.Eq(candidate => candidate.RevisionId,
                                                                      claim.RevisionId));
            FilterDefinition<DocumentArtifactBlobRecord> exactUpload =
                Builders<DocumentArtifactBlobRecord>.Filter.And(
                    Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.Id, record.Id),
                    Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.GridFsId,
                                                                     record.GridFsId),
                    Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.ClaimSchemaVersion,
                                                                     CurrentArtifactClaimSchemaVersion),
                    Builders<DocumentArtifactBlobRecord>.Filter.ElemMatch(artifact => artifact.Claims,
                                                                           exactClaim));
            UpdateDefinition<DocumentArtifactBlobRecord> release =
                Builders<DocumentArtifactBlobRecord>.Update.PullFilter(artifact => artifact.Claims,
                                                                        exactClaim);
            await mContext.DocumentArtifactBlobs.UpdateOneAsync(exactUpload,
                                                                release,
                                                                cancellationToken: ct);
            await TryDeleteManagedArtifactAsync(record.Id, record.GridFsId, ct);
        }
    }

    private async Task ReleaseArtifactClaimsAsync(
        string revisionId,
        IReadOnlyList<DocumentRevisionArtifactClaim> claims,
        CancellationToken ct)
    {
        ExceptionDispatchInfo? failure = null;
        foreach(DocumentRevisionArtifactClaim claim in claims.DistinctBy(item =>
                     (item.ArtifactHash, item.ClaimId)))
        {
            try
            {
                CancellationToken currentToken = CurrentCleanupToken(failure, ct);
                await TryReleaseArtifactClaimAsync(claim.ArtifactHash,
                                                   revisionId,
                                                   claim.ClaimId,
                                                   currentToken);
                await TryDeleteManagedArtifactAsync(claim.ArtifactHash, currentToken);
            }
            catch(Exception ex)
            {
                failure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        failure?.Throw();
    }

    private async Task<bool> TryFinalizeArtifactClaimAsync(string artifactHash,
                                                            string revisionId,
                                                            string claimId,
                                                            DateTime finalizedAtUtc,
                                                            CancellationToken ct)
    {
        FilterDefinition<DocumentArtifactBlobRecord> filter = ExactActiveClaimFilter(artifactHash,
                                                                                      revisionId,
                                                                                      claimId);
        UpdateDefinition<DocumentArtifactBlobRecord> update =
            Builders<DocumentArtifactBlobRecord>.Update
                                                    .Set(ArtifactClaimFinalizedAtFieldPath, finalizedAtUtc)
                                                    .Unset(ArtifactClaimExpiresAtFieldPath);
        UpdateResult finalized = await mContext.DocumentArtifactBlobs.UpdateOneAsync(filter,
                                                                                      update,
                                                                                      cancellationToken: ct);
        bool result = finalized.MatchedCount == 1;
        return result;
    }

    private async Task<bool> TryReleaseArtifactClaimAsync(string artifactHash,
                                                           string revisionId,
                                                           string claimId,
                                                           CancellationToken ct)
    {
        FilterDefinition<DocumentArtifactClaimRecord> claimFilter =
            Builders<DocumentArtifactClaimRecord>.Filter.And(
                Builders<DocumentArtifactClaimRecord>.Filter.Eq(claim => claim.ClaimId, claimId),
                Builders<DocumentArtifactClaimRecord>.Filter.Eq(claim => claim.RevisionId, revisionId));
        FilterDefinition<DocumentArtifactBlobRecord> filter =
            Builders<DocumentArtifactBlobRecord>.Filter.And(
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.Id, artifactHash),
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.ClaimSchemaVersion,
                                                                 CurrentArtifactClaimSchemaVersion));
        UpdateDefinition<DocumentArtifactBlobRecord> update =
            Builders<DocumentArtifactBlobRecord>.Update.PullFilter(artifact => artifact.Claims, claimFilter);
        UpdateResult released = await mContext.DocumentArtifactBlobs.UpdateOneAsync(filter,
                                                                                     update,
                                                                                     cancellationToken: ct);
        return released.ModifiedCount == 1;
    }

    internal async Task<bool> TryReleaseObservedArtifactClaimAsync(
        string artifactHash,
        DocumentArtifactClaimRecord observedClaim,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactHash);
        ArgumentNullException.ThrowIfNull(observedClaim);
        FilterDefinition<DocumentArtifactClaimRecord> exactObservedClaim =
            Builders<DocumentArtifactClaimRecord>.Filter.And(
                Builders<DocumentArtifactClaimRecord>.Filter.Eq(claim => claim.ClaimId,
                                                                  observedClaim.ClaimId),
                Builders<DocumentArtifactClaimRecord>.Filter.Eq(claim => claim.RevisionId,
                                                                  observedClaim.RevisionId),
                Builders<DocumentArtifactClaimRecord>.Filter.Eq(claim => claim.PreparedAtUtc,
                                                                  observedClaim.PreparedAtUtc),
                Builders<DocumentArtifactClaimRecord>.Filter.Eq(claim => claim.ExpiresAtUtc,
                                                                  observedClaim.ExpiresAtUtc),
                Builders<DocumentArtifactClaimRecord>.Filter.Eq(claim => claim.FinalizedAtUtc,
                                                                  observedClaim.FinalizedAtUtc));
        FilterDefinition<DocumentArtifactBlobRecord> filter =
            Builders<DocumentArtifactBlobRecord>.Filter.And(
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.Id, artifactHash),
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.ClaimSchemaVersion,
                                                                 CurrentArtifactClaimSchemaVersion),
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.DeletionId, value: null),
                Builders<DocumentArtifactBlobRecord>.Filter.ElemMatch(artifact => artifact.Claims,
                                                                       exactObservedClaim));
        UpdateDefinition<DocumentArtifactBlobRecord> release =
            Builders<DocumentArtifactBlobRecord>.Update.PullFilter(artifact => artifact.Claims,
                                                                    exactObservedClaim);
        UpdateResult released = await mContext.DocumentArtifactBlobs.UpdateOneAsync(filter,
                                                                                     release,
                                                                                     cancellationToken: ct);
        return released.ModifiedCount == 1;
    }

    private static FilterDefinition<DocumentArtifactBlobRecord> ExactActiveClaimFilter(
        string artifactHash,
        string revisionId,
        string claimId)
    {
        FilterDefinition<DocumentArtifactBlobRecord> exactClaim =
            Builders<DocumentArtifactBlobRecord>.Filter.ElemMatch(
                artifact => artifact.Claims,
                claim => claim.ClaimId == claimId && claim.RevisionId == revisionId);
        FilterDefinition<DocumentArtifactBlobRecord> result =
            Builders<DocumentArtifactBlobRecord>.Filter.And(
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.Id, artifactHash),
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.ClaimSchemaVersion,
                                                                 CurrentArtifactClaimSchemaVersion),
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.DeletionId, value: null),
                exactClaim);
        return result;
    }

    private async Task<long> DeleteRevisionsAsync(FilterDefinition<DocumentRevisionRecord> filter,
                                                   CancellationToken ct)
    {
        long result = 0;
        DocumentRevisionRecord? deleted;
        do
        {
            deleted = await mContext.DocumentRevisions.FindOneAndDeleteAsync(filter,
                                                                              cancellationToken: ct);
            if (deleted != null)
            {
                result++;
                await ReleaseArtifactClaimsAsync(deleted.Id,
                                                 deleted.ArtifactClaims,
                                                 CancellationToken.None);
            }
        } while(deleted != null);

        return result;
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
                CancellationToken cleanupToken = CurrentCleanupToken(failure, ct);
                var reference = await mContext.DocumentRevisions.Find(referenceFilter)
                                              .Limit(1)
                                              .FirstOrDefaultAsync(cleanupToken);
                if (reference == null)
                {
                    var filter = Builders<SourceDocumentRecord>.Filter.And(
                        Builders<SourceDocumentRecord>.Filter.Eq(d => d.Id, document.Id),
                        Builders<SourceDocumentRecord>.Filter.Eq(d => d.LibraryId, libraryId));
                    await mContext.SourceDocuments.DeleteOneAsync(filter, cleanupToken);
                }
                else
                {
                    var publishedFilter = Builders<DocumentRevisionRecord>.Filter.And(
                        referenceFilter,
                        Builders<DocumentRevisionRecord>.Filter.Eq(
                            revision => revision.State,
                            DocumentRevisionState.Published));
                    DocumentRevisionRecord? latestPublished =
                        await mContext.DocumentRevisions.Find(publishedFilter)
                                      .SortByDescending(revision => revision.PublishedAtUtc)
                                      .ThenByDescending(revision => revision.AcquiredAtUtc)
                                      .FirstOrDefaultAsync(cleanupToken);
                    var sourceFilter = Builders<SourceDocumentRecord>.Filter.And(
                        Builders<SourceDocumentRecord>.Filter.Eq(d => d.Id, document.Id),
                        Builders<SourceDocumentRecord>.Filter.Eq(d => d.LibraryId, libraryId));
                    UpdateDefinition<SourceDocumentRecord> provenanceUpdate = latestPublished == null
                        ? Builders<SourceDocumentRecord>.Update
                            .Unset(d => d.LastSeenVersion)
                            .Unset(d => d.UpdatedAtUtc)
                        : Builders<SourceDocumentRecord>.Update
                            .Set(d => d.LastSeenVersion, latestPublished.Version)
                            .Set(d => d.UpdatedAtUtc,
                                 latestPublished.PublishedAtUtc ?? latestPublished.AcquiredAtUtc);
                    await mContext.SourceDocuments.UpdateOneAsync(sourceFilter,
                                                                 provenanceUpdate,
                                                                 cancellationToken: cleanupToken);
                }
            }
            catch(Exception ex)
            {
                failure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        failure?.Throw();
    }

    private Task<bool> TryDeleteManagedArtifactAsync(string sha256, CancellationToken ct) =>
        TryDeleteManagedArtifactAsync(sha256, expectedGridFsId: null, ct);

    private async Task<bool> TryDeleteManagedArtifactAsync(string sha256,
                                                            string? expectedGridFsId,
                                                            CancellationToken ct)
    {
        string deletionId = Guid.NewGuid().ToString("N");
        FilterDefinition<DocumentArtifactBlobRecord> filter =
            Builders<DocumentArtifactBlobRecord>.Filter.And(
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.Id, sha256),
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.ClaimSchemaVersion,
                                                                 CurrentArtifactClaimSchemaVersion),
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.DeletionId, value: null),
                Builders<DocumentArtifactBlobRecord>.Filter.Size(ArtifactClaimsFieldName, 0));
        if (expectedGridFsId != null)
        {
            filter = Builders<DocumentArtifactBlobRecord>.Filter.And(
                filter,
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.GridFsId,
                                                                 expectedGridFsId));
        }
        UpdateDefinition<DocumentArtifactBlobRecord> update =
            Builders<DocumentArtifactBlobRecord>.Update
                                                    .Set(artifact => artifact.DeletionId, deletionId)
                                                    .Set(artifact => artifact.DeletionPreparedAtUtc,
                                                         DateTime.UtcNow);
        var options = new FindOneAndUpdateOptions<DocumentArtifactBlobRecord>
                          {
                              ReturnDocument = ReturnDocument.After
                          };
        DocumentArtifactBlobRecord? tombstone = await mContext.DocumentArtifactBlobs
                                                               .FindOneAndUpdateAsync(filter,
                                                                                      update,
                                                                                      options,
                                                                                      ct);
        if (tombstone == null)
        {
            FilterDefinition<DocumentArtifactBlobRecord> tombstoneFilter =
                Builders<DocumentArtifactBlobRecord>.Filter.And(
                    Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.Id, sha256),
                    Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.ClaimSchemaVersion,
                                                                     CurrentArtifactClaimSchemaVersion),
                    Builders<DocumentArtifactBlobRecord>.Filter.Ne(artifact => artifact.DeletionId,
                                                                     value: null));
            if (expectedGridFsId != null)
            {
                tombstoneFilter = Builders<DocumentArtifactBlobRecord>.Filter.And(
                    tombstoneFilter,
                    Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.GridFsId,
                                                                     expectedGridFsId));
            }
            tombstone = await mContext.DocumentArtifactBlobs.Find(tombstoneFilter)
                                      .FirstOrDefaultAsync(ct);
        }

        bool result = tombstone != null && await CompleteArtifactDeletionAsync(tombstone, ct);
        return result;
    }

    private async Task<bool> CompleteArtifactDeletionAsync(DocumentArtifactBlobRecord tombstone,
                                                            CancellationToken ct)
    {
        bool validTombstone = tombstone.ClaimSchemaVersion == CurrentArtifactClaimSchemaVersion &&
                              !string.IsNullOrEmpty(tombstone.DeletionId);
        bool result = false;
        if (validTombstone)
        {
            if (!ObjectId.TryParse(tombstone.GridFsId, out var fileId))
                throw new InvalidDataException($"Managed artifact '{tombstone.Id}' has an invalid GridFS id.");

            await DeleteManagedGridFsFileAsync(fileId, ct);
            FilterDefinition<DocumentArtifactBlobRecord> filter =
                Builders<DocumentArtifactBlobRecord>.Filter.And(
                    Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.Id, tombstone.Id),
                    Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.ClaimSchemaVersion,
                                                                     CurrentArtifactClaimSchemaVersion),
                    Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.GridFsId,
                                                                     tombstone.GridFsId),
                    Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.DeletionId,
                                                                     tombstone.DeletionId));
            DeleteResult deleted = await mContext.DocumentArtifactBlobs.DeleteOneAsync(filter, ct);
            result = deleted.DeletedCount == 1;
        }

        return result;
    }

    private async Task DeleteManagedGridFsFileAsync(ObjectId fileId, CancellationToken ct)
    {
        try
        {
            await mContext.DocumentArtifactsBucket.DeleteAsync(fileId, ct);
        }
        catch(GridFSFileNotFoundException)
        {
            // A previous attempt already removed the exact bytes.
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

    private async Task ReplaceDirectoryDefinitionWhenLeaseAvailableAsync(
        DirectoryLibraryDefinition definition,
        CancellationToken ct)
    {
        bool stored = false;
        string registrationIncarnationId = CreateRegistrationIncarnationId();
        DirectoryLibraryDefinition replacement = definition with
                                                     {
                                                         RegistrationIncarnationId = registrationIncarnationId,
                                                         PublicationLeaseScanRunId = null,
                                                         PublicationLeaseRegistrationRevision = null,
                                                         PublicationLeaseExpiresAtUtc = null
                                                     };
        while(!stored)
        {
            FilterDefinition<DirectoryLibraryDefinition> filter =
                Builders<DirectoryLibraryDefinition>.Filter.And(
                    Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.Id, definition.Id),
                    NormalLifecycleFilter(),
                    AvailablePublicationLeaseFilter(mTimeProvider.GetUtcNow().UtcDateTime));
            ReplaceOneResult replaced = await mContext.DirectoryLibraries.ReplaceOneAsync(filter,
                                                                                             replacement,
                                                                                             cancellationToken: ct);
            stored = replaced.MatchedCount == 1;
            if (!stored)
            {
                DirectoryLibraryDefinition? inserted =
                    await TryInsertDirectoryDefinitionOrWaitAsync(replacement, ct);
                stored = inserted != null;
            }
        }
    }

    private async Task<DirectoryLibraryDefinition> RegisterDirectoryDefinitionWhenLeaseAvailableAsync(
        DirectoryLibraryDefinition definition,
        CancellationToken ct)
    {
        DirectoryLibraryDefinition? result = null;
        string registrationIncarnationId = CreateRegistrationIncarnationId();
        var insertion = definition with
                            {
                                RegistrationRevision = 1,
                                RegistrationIncarnationId = registrationIncarnationId,
                                LastPublishedAtUtc = null,
                                LastPublishedVersion = null,
                                PublicationLeaseScanRunId = null,
                                PublicationLeaseRegistrationRevision = null,
                                PublicationLeaseExpiresAtUtc = null
                            };
        while(result == null)
        {
            FilterDefinition<DirectoryLibraryDefinition> filter =
                Builders<DirectoryLibraryDefinition>.Filter.And(
                    Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.Id, definition.Id),
                    NormalLifecycleFilter(),
                    AvailablePublicationLeaseFilter(mTimeProvider.GetUtcNow().UtcDateTime));
            UpdateDefinition<DirectoryLibraryDefinition> update =
                Builders<DirectoryLibraryDefinition>.Update
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
                                                    .Set(item => item.RegistrationIncarnationId,
                                                         registrationIncarnationId)
                                                    .Set(item => item.LastPublishedAtUtc, value: null)
                                                    .Set(item => item.LastPublishedVersion, value: null)
                                                    .Set(item => item.PublicationLeaseScanRunId, value: null)
                                                    .Set(item => item.PublicationLeaseRegistrationRevision,
                                                         value: null)
                                                    .Set(item => item.PublicationLeaseExpiresAtUtc, value: null)
                                                    .Inc(item => item.RegistrationRevision, value: 1);
            var options = new FindOneAndUpdateOptions<DirectoryLibraryDefinition>
                              {
                                  ReturnDocument = ReturnDocument.After
                              };
            result = await mContext.DirectoryLibraries.FindOneAndUpdateAsync(filter,
                                                                              update,
                                                                               options,
                                                                               ct);
            if (result == null)
                result = await TryInsertDirectoryDefinitionOrWaitAsync(insertion, ct);
        }

        return result;
    }

    private async Task<DirectoryLibraryDefinition?> TryInsertDirectoryDefinitionOrWaitAsync(
        DirectoryLibraryDefinition definition,
        CancellationToken ct)
    {
        DirectoryLibraryDefinition? result;
        DirectoryLibraryDefinition? existing = await mContext.DirectoryLibraries
                                                             .Find(item => item.Id == definition.Id)
                                                             .FirstOrDefaultAsync(ct);
        if (existing == null)
        {
            bool inserted = await TryInsertDirectoryDefinitionAsync(definition, ct);
            result = inserted ? definition : null;
        }
        else
        {
            ThrowIfPendingRename(existing);
            await Task.Delay(PublicationLeaseRetryDelayMilliseconds, ct);
            result = null;
        }

        return result;
    }

    private static void ThrowIfPendingRename(DirectoryLibraryDefinition definition)
    {
        if (definition.PendingRenameOperationId != null)
            throw new InvalidOperationException(PendingRenameBusyDetail);
    }

    private async Task<bool> TryInsertDirectoryDefinitionAsync(DirectoryLibraryDefinition definition,
                                                                CancellationToken ct)
    {
        bool result;
        try
        {
            await mContext.DirectoryLibraries.InsertOneAsync(definition, cancellationToken: ct);
            result = true;
        }
        catch(MongoException ex) when (IsDuplicateKey(ex))
        {
            // A registration or lease won the insert race; retry its current state.
            result = false;
        }

        return result;
    }

    private async ValueTask ReleaseDirectoryPublicationLeaseAsync(string libraryId,
                                                                   string scanRunId,
                                                                   string? registrationIncarnationId,
                                                                   long registrationRevision)
    {
        FilterDefinition<DirectoryLibraryDefinition> filter =
            OwnedPublicationLeaseFilter(libraryId,
                                        scanRunId,
                                        registrationIncarnationId,
                                        registrationRevision,
                                        requireUnexpired: false);
        UpdateDefinition<DirectoryLibraryDefinition> update =
            Builders<DirectoryLibraryDefinition>.Update
                                                .Set(item => item.PublicationLeaseScanRunId, value: null)
                                                .Set(item => item.PublicationLeaseRegistrationRevision, value: null)
                                                .Set(item => item.PublicationLeaseExpiresAtUtc, value: null);
        await mContext.DirectoryLibraries.UpdateOneAsync(filter,
                                                          update,
                                                          cancellationToken: CancellationToken.None);
    }

    private async ValueTask<bool> TryRenewDirectoryPublicationLeaseAsync(string libraryId,
                                                                          string scanRunId,
                                                                          string? registrationIncarnationId,
                                                                          long registrationRevision,
                                                                          CancellationToken ct)
    {
        DateTime renewedAtUtc = mTimeProvider.GetUtcNow().UtcDateTime;
        FilterDefinition<DirectoryLibraryDefinition> filter =
            OwnedPublicationLeaseFilter(libraryId,
                                        scanRunId,
                                        registrationIncarnationId,
                                        registrationRevision,
                                        renewedAtUtc);
        DateTime expiresAtUtc = renewedAtUtc.Add(mPublicationLeaseDuration);
        UpdateDefinition<DirectoryLibraryDefinition> update =
            Builders<DirectoryLibraryDefinition>.Update.Set(item => item.PublicationLeaseExpiresAtUtc,
                                                              expiresAtUtc);
        UpdateResult renewed = await mContext.DirectoryLibraries.UpdateOneAsync(filter,
                                                                                 update,
                                                                                 cancellationToken: ct);
        bool result = renewed.MatchedCount == 1;
        return result;
    }

    private async ValueTask<bool> TryDeleteDirectoryDefinitionAsync(string libraryId,
                                                                     string scanRunId,
                                                                     string? registrationIncarnationId,
                                                                     long registrationRevision,
                                                                     CancellationToken ct)
    {
        DateTime deletedAtUtc = mTimeProvider.GetUtcNow().UtcDateTime;
        FilterDefinition<DirectoryLibraryDefinition> filter =
            OwnedPublicationLeaseFilter(libraryId,
                                        scanRunId,
                                        registrationIncarnationId,
                                        registrationRevision,
                                        deletedAtUtc);
        DeleteResult deleted = await mContext.DirectoryLibraries.DeleteOneAsync(filter, ct);
        bool result = deleted.DeletedCount == 1;
        return result;
    }

    private FilterDefinition<DirectoryLibraryDefinition> OwnedPublicationLeaseFilter(
        string libraryId,
        string scanRunId,
        string? registrationIncarnationId,
        long registrationRevision,
        bool requireUnexpired)
    {
        var filters = new List<FilterDefinition<DirectoryLibraryDefinition>>
                          {
                              Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.Id, libraryId),
                              Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.RegistrationRevision,
                                  registrationRevision),
                              Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.RegistrationIncarnationId,
                                  registrationIncarnationId),
                              Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.PublicationLeaseScanRunId,
                                  scanRunId),
                              Builders<DirectoryLibraryDefinition>.Filter.Eq(
                                  item => item.PublicationLeaseRegistrationRevision,
                                  registrationRevision),
                              NormalLifecycleFilter()
                          };
        if (requireUnexpired)
        {
            filters.Add(Builders<DirectoryLibraryDefinition>.Filter.Gt(
                            item => item.PublicationLeaseExpiresAtUtc,
                            mTimeProvider.GetUtcNow().UtcDateTime));
        }

        return Builders<DirectoryLibraryDefinition>.Filter.And(filters);
    }

    private static FilterDefinition<DirectoryLibraryDefinition> OwnedPublicationLeaseFilter(
        string libraryId,
        string scanRunId,
        string? registrationIncarnationId,
        long registrationRevision,
        DateTime nowUtc) =>
        Builders<DirectoryLibraryDefinition>.Filter.And(
            Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.Id, libraryId),
            Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.RegistrationRevision,
                                                             registrationRevision),
            Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.RegistrationIncarnationId,
                                                             registrationIncarnationId),
            Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.PublicationLeaseScanRunId,
                                                             scanRunId),
            Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.PublicationLeaseRegistrationRevision,
                                                             registrationRevision),
            Builders<DirectoryLibraryDefinition>.Filter.Gt(item => item.PublicationLeaseExpiresAtUtc,
                                                             nowUtc),
            NormalLifecycleFilter());

    private static FilterDefinition<DirectoryLibraryDefinition> NormalLifecycleFilter() =>
        Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.PendingRenameOperationId, value: null);

    private static FilterDefinition<DirectoryLibraryDefinition> AvailablePublicationLeaseFilter(DateTime nowUtc) =>
        Builders<DirectoryLibraryDefinition>.Filter.Or(
            Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.PublicationLeaseScanRunId, value: null),
            Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.PublicationLeaseExpiresAtUtc, value: null),
            Builders<DirectoryLibraryDefinition>.Filter.Lte(item => item.PublicationLeaseExpiresAtUtc, nowUtc));

    private sealed class MongoDirectoryPublicationLease : IDirectoryPublicationLease
    {
        internal MongoDirectoryPublicationLease(SourceDocumentRepository owner,
                                                string libraryId,
                                                string scanRunId,
                                                string? registrationIncarnationId,
                                                long registrationRevision)
        {
            mOwner = owner;
            LibraryId = libraryId;
            ScanRunId = scanRunId;
            RegistrationIncarnationId = registrationIncarnationId;
            RegistrationRevision = registrationRevision;
            mRenewalInterval = owner.mPublicationLeaseRenewalInterval;
            mTimeProvider = owner.mTimeProvider;
            mHeartbeatTask = MaintainOwnershipAsync();
        }

        private int mDefinitionDeleted;
        private int mDisposed;
        private readonly CancellationTokenSource mHeartbeatStop = new();
        private readonly Task mHeartbeatTask;
        private readonly SemaphoreSlim mMutationGate = new(initialCount: 1, maxCount: 1);
        private SourceDocumentRepository? mOwner;
        private readonly CancellationTokenSource mOwnershipLost = new();
        private int mOwnershipLostSignaled;
        private readonly TimeSpan mRenewalInterval;
        private readonly TimeProvider mTimeProvider;

        /// <inheritdoc />
        public string LibraryId { get; }

        /// <inheritdoc />
        public string ScanRunId { get; }

        /// <inheritdoc />
        public string? RegistrationIncarnationId { get; }

        /// <inheritdoc />
        public long RegistrationRevision { get; }

        /// <inheritdoc />
        public CancellationToken OwnershipLostToken => mOwnershipLost.Token;

        /// <inheritdoc />
        public async ValueTask<bool> TryRenewAsync(CancellationToken ct = default)
        {
            bool result;
            try
            {
                result = await TryRenewSerializedAsync(ct);
            }
            catch(OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                SignalOwnershipLost();
                throw;
            }

            if (!result)
                SignalOwnershipLost();
            return result;
        }

        internal async ValueTask<bool> TryDeleteDefinitionAsync(CancellationToken ct)
        {
            await mMutationGate.WaitAsync(ct);
            bool result = false;
            bool ownershipLost = false;
            try
            {
                SourceDocumentRepository? owner = mOwner;
                bool active = Volatile.Read(ref mDisposed) == 0 &&
                              Volatile.Read(ref mDefinitionDeleted) == 0 &&
                              !mOwnershipLost.IsCancellationRequested &&
                              owner != null;
                if (active && owner != null)
                {
                    result = await owner.TryDeleteDirectoryDefinitionAsync(LibraryId,
                                                                            ScanRunId,
                                                                            RegistrationIncarnationId,
                                                                            RegistrationRevision,
                                                                            ct);
                }

                if (result)
                {
                    Interlocked.Exchange(ref mDefinitionDeleted, value: 1);
                    await mHeartbeatStop.CancelAsync();
                }
                else
                {
                    ownershipLost = true;
                }
            }
            finally
            {
                mMutationGate.Release();
            }

            if (ownershipLost)
                SignalOwnershipLost();
            return result;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            bool firstDisposal = Interlocked.Exchange(ref mDisposed, value: 1) == 0;
            if (firstDisposal)
            {
                await mHeartbeatStop.CancelAsync();
                await mHeartbeatTask;
                await mMutationGate.WaitAsync(CancellationToken.None);
                try
                {
                    SourceDocumentRepository? owner = Interlocked.Exchange(ref mOwner, value: null);
                    if (owner != null && Volatile.Read(ref mDefinitionDeleted) == 0)
                    {
                        await owner.ReleaseDirectoryPublicationLeaseAsync(LibraryId,
                                                                           ScanRunId,
                                                                           RegistrationIncarnationId,
                                                                           RegistrationRevision);
                    }
                }
                finally
                {
                    mMutationGate.Release();
                    SignalOwnershipLost();
                    mHeartbeatStop.Dispose();
                }
            }
        }

        private async Task MaintainOwnershipAsync()
        {
            using var timer = new PeriodicTimer(mRenewalInterval, mTimeProvider);
            try
            {
                while(await timer.WaitForNextTickAsync(mHeartbeatStop.Token))
                {
                    bool renewed;
                    try
                    {
                        renewed = await TryRenewSerializedAsync(mHeartbeatStop.Token);
                    }
                    catch(OperationCanceledException) when (mHeartbeatStop.IsCancellationRequested)
                    {
                        break;
                    }
                    catch
                    {
                        SignalOwnershipLost();
                        break;
                    }

                    if (!renewed)
                    {
                        SignalOwnershipLost();
                        break;
                    }
                }
            }
            catch(OperationCanceledException) when (mHeartbeatStop.IsCancellationRequested)
            {
                // Normal lease disposal or final definition deletion.
            }
        }

        private void SignalOwnershipLost()
        {
            if (Interlocked.Exchange(ref mOwnershipLostSignaled, value: 1) == 0)
                mOwnershipLost.Cancel();
        }

        private async ValueTask<bool> TryRenewSerializedAsync(CancellationToken ct)
        {
            await mMutationGate.WaitAsync(ct);
            bool result = false;
            try
            {
                SourceDocumentRepository? owner = mOwner;
                bool active = Volatile.Read(ref mDisposed) == 0 &&
                              Volatile.Read(ref mDefinitionDeleted) == 0 &&
                              !mOwnershipLost.IsCancellationRequested &&
                              owner != null;
                if (active && owner != null)
                {
                    result = await owner.TryRenewDirectoryPublicationLeaseAsync(LibraryId,
                                                                                 ScanRunId,
                                                                                 RegistrationIncarnationId,
                                                                                 RegistrationRevision,
                                                                                 ct);
                }
            }
            finally
            {
                mMutationGate.Release();
            }

            return result;
        }
    }

    private static string CreateRegistrationIncarnationId() => Guid.NewGuid().ToString("N");

    private static void ValidateLease(IDirectoryPublicationLease lease)
    {
        ArgumentException.ThrowIfNullOrEmpty(lease.LibraryId);
        ArgumentException.ThrowIfNullOrEmpty(lease.ScanRunId);
        if (lease.RegistrationRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(lease));
        if (lease.RegistrationIncarnationId is { Length: 0 })
            throw new ArgumentException("The registration incarnation cannot be empty.", nameof(lease));
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

    private static bool IsWellFormedClaim(DocumentArtifactClaimRecord claim) =>
        !string.IsNullOrEmpty(claim.ClaimId) &&
        !string.IsNullOrEmpty(claim.RevisionId) &&
        claim.PreparedAtUtc.Kind == DateTimeKind.Utc;

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

    private sealed record ArtifactRecoveryCounts(long ClaimsFinalized,
                                                  long ClaimsReleased,
                                                  long ArtifactDeletionsCompleted)
    {
        internal ArtifactRecoveryCounts Add(ArtifactRecoveryCounts other)
        {
            ArgumentNullException.ThrowIfNull(other);
            return new ArtifactRecoveryCounts(ClaimsFinalized + other.ClaimsFinalized,
                                              ClaimsReleased + other.ClaimsReleased,
                                              ArtifactDeletionsCompleted + other.ArtifactDeletionsCompleted);
        }

        internal static ArtifactRecoveryCounts Empty { get; } = new(0, 0, 0);
    }

    private const int Sha256HexLength = 64;
    private const int DuplicateKeyErrorCode = 11000;
    private const int CurrentArtifactClaimSchemaVersion = DocumentArtifactBlobRecord.CurrentClaimSchemaVersion;
    private const int MaximumArtifactClaimsPerBlob = 10000;
    private const int PublicationLeaseRetryDelayMilliseconds = 25;
    private const string PendingRenameBusyDetail =
        "The directory library is unavailable while its pending rename is recovered or completed.";
    private const string PendingRenameInputDetail =
        "Normal directory registration cannot supply a pending rename operation.";
    private const char UnitSeparator = '\u001f';
    private const string ArtifactClaimsFieldName = "Claims";
    private const string ArtifactClaimExpiresAtFieldPath = "Claims.$.ExpiresAtUtc";
    private const string ArtifactClaimFinalizedAtFieldPath = "Claims.$.FinalizedAtUtc";
    private static readonly TimeSpan smArtifactClaimPreparationLease = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan smPublicationLeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan smPublicationLeaseRenewalInterval = TimeSpan.FromMinutes(1);
}
