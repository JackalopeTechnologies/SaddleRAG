// SourceDocumentRepositoryIntegrationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Tests.Database;

[Trait("Category", "Integration")]
public sealed class SourceDocumentRepositoryIntegrationTests : IAsyncLifetime
{
    private string mDatabaseName = string.Empty;
    private SaddleRagDbContext mContext = new SaddleRagDbContext(Options.Create(new SaddleRagDbSettings()));
    private SourceDocumentRepository mRepository = null!;

    public async ValueTask InitializeAsync()
    {
        mDatabaseName = $"saddlerag-documents-{Guid.NewGuid():N}";
        mContext = new SaddleRagDbContext(Options.Create(new SaddleRagDbSettings
                                                            {
                                                                ConnectionString = TestConnectionString,
                                                                DatabaseName = mDatabaseName
                                                            }));
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        mRepository = new SourceDocumentRepository(mContext);
    }

    public async ValueTask DisposeAsync()
    {
        await mContext.Database.Client.DropDatabaseAsync(mDatabaseName);
    }

    [Fact]
    public async Task DirectoryAndPathIdentityRoundTripWithoutContentIdentityCollapse()
    {
        var definition = Definition("manual-library", "C:\\Docs");
        await mRepository.UpsertDirectoryDefinitionAsync(definition, TestContext.Current.CancellationToken);

        var firstCandidate = Document("manual-library", "manuals/guide.pdf", "first-id");
        var samePathCandidate = Document("manual-library", "manuals/guide.pdf", "losing-id");
        var otherPathCandidate = Document("manual-library", "archive/guide.pdf", "second-id");
        var first = await mRepository.GetOrCreateDocumentAsync(firstCandidate, TestContext.Current.CancellationToken);
        var samePath = await mRepository.GetOrCreateDocumentAsync(samePathCandidate, TestContext.Current.CancellationToken);
        var otherPath = await mRepository.GetOrCreateDocumentAsync(otherPathCandidate, TestContext.Current.CancellationToken);

        DirectoryLibraryDefinition? storedDefinition = await mRepository.GetDirectoryDefinitionAsync(
                                                                  definition.Id,
                                                                  TestContext.Current.CancellationToken);
        Assert.NotNull(storedDefinition);
        Assert.False(string.IsNullOrWhiteSpace(storedDefinition.RegistrationIncarnationId));
        Assert.Equivalent(definition with
                              {
                                  RegistrationIncarnationId = storedDefinition.RegistrationIncarnationId
                              },
                          storedDefinition,
                          strict: true);
        Assert.Equal(first.Id, samePath.Id);
        Assert.NotEqual(first.Id, otherPath.Id);
    }

    [Fact]
    public async Task UnboundDefinitionMayPersistWithoutRootWhileBoundDefinitionStillRequiresRoot()
    {
        DirectoryLibraryDefinition unbound = Definition("portable-library", string.Empty) with
                                                 {
                                                     BindingStatus = DirectoryLibraryBindingStatus.Unbound
                                                 };

        await mRepository.UpsertDirectoryDefinitionAsync(unbound, TestContext.Current.CancellationToken);

        DirectoryLibraryDefinition stored = Assert.IsType<DirectoryLibraryDefinition>(
            await mRepository.GetDirectoryDefinitionAsync(unbound.Id,
                                                           TestContext.Current.CancellationToken));
        Assert.Equal(DirectoryLibraryBindingStatus.Unbound, stored.BindingStatus);
        Assert.Empty(stored.RootPath);

        DirectoryLibraryDefinition bound = unbound with
                                               {
                                                   Id = "invalid-bound-library",
                                                   BindingStatus = DirectoryLibraryBindingStatus.Bound
                                               };
        await Assert.ThrowsAsync<ArgumentException>(() => mRepository.UpsertDirectoryDefinitionAsync(
                                                           bound,
                                                           TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentReregistrationRejectsStalePublicationMetadataUpdate()
    {
        DirectoryLibraryDefinition first = await mRepository.RegisterDirectoryDefinitionAsync(
                                               Definition("manual-library", "C:\\Docs"),
                                               TestContext.Current.CancellationToken);
        IDirectoryPublicationLease firstLease = Assert.IsAssignableFrom<IDirectoryPublicationLease>(
            await mRepository.TryAcquireDirectoryPublicationLeaseAsync(
                first.Id,
                first.RegistrationRevision,
                first.RegistrationIncarnationId,
                "stale-scan",
                expectedPublishedVersion: null,
                TestContext.Current.CancellationToken));
        await firstLease.DisposeAsync();
        DirectoryLibraryDefinition second = await mRepository.RegisterDirectoryDefinitionAsync(
                                                Definition("manual-library", "D:\\NewDocs"),
                                                TestContext.Current.CancellationToken);
        var publishedAt = new DateTime(year: 2026,
                                       month: 8,
                                       day: 4,
                                       hour: 18,
                                       minute: 0,
                                       second: 0,
                                       DateTimeKind.Utc);

        bool staleUpdated = await mRepository.TryUpdateDirectoryPublicationAsync(
                                firstLease,
                                expectedPublishedVersion: null,
                                publishedAt,
                                "2026-08-04",
                                TestContext.Current.CancellationToken);
        IDirectoryPublicationLease? lease = await mRepository.TryAcquireDirectoryPublicationLeaseAsync(
                                                       second.Id,
                                                       second.RegistrationRevision,
                                                       second.RegistrationIncarnationId,
                                                       "current-scan",
                                                       expectedPublishedVersion: null,
                                                       TestContext.Current.CancellationToken);
        IDirectoryPublicationLease currentLease = Assert.IsAssignableFrom<IDirectoryPublicationLease>(lease);
        bool currentUpdated;
        await using (currentLease)
        {
            currentUpdated = await mRepository.TryUpdateDirectoryPublicationAsync(
                                 currentLease,
                                 expectedPublishedVersion: null,
                                 publishedAt,
                                 "2026-08-04",
                                 TestContext.Current.CancellationToken);
        }
        DirectoryLibraryDefinition? stored = await mRepository.GetDirectoryDefinitionAsync(
                                                       second.Id,
                                                       TestContext.Current.CancellationToken);

        Assert.Equal(1, first.RegistrationRevision);
        Assert.Equal(2, second.RegistrationRevision);
        Assert.NotEqual(first.RegistrationIncarnationId, second.RegistrationIncarnationId);
        Assert.False(staleUpdated);
        Assert.True(currentUpdated);
        Assert.NotNull(stored);
        Assert.Equal("D:\\NewDocs", stored.RootPath);
        Assert.Equal(second.RegistrationRevision, stored.RegistrationRevision);
        Assert.Equal("2026-08-04", stored.LastPublishedVersion);
    }

    [Fact]
    public async Task ReregistrationWaitsForDurablePublicationLeaseRelease()
    {
        DirectoryLibraryDefinition first = await mRepository.RegisterDirectoryDefinitionAsync(
                                               Definition("manual-library", "C:\\Docs"),
                                               TestContext.Current.CancellationToken);
        IDirectoryPublicationLease? lease = await mRepository.TryAcquireDirectoryPublicationLeaseAsync(
                                                       first.Id,
                                                       first.RegistrationRevision,
                                                       first.RegistrationIncarnationId,
                                                       "publishing-scan",
                                                       expectedPublishedVersion: null,
                                                       TestContext.Current.CancellationToken);
        IDirectoryPublicationLease publicationLease = Assert.IsAssignableFrom<IDirectoryPublicationLease>(lease);
        Task<DirectoryLibraryDefinition> registration = mRepository.RegisterDirectoryDefinitionAsync(
                                                            Definition("manual-library", "D:\\NewDocs"),
                                                            TestContext.Current.CancellationToken);
        await Task.Yield();
        Assert.False(registration.IsCompleted);

        await publicationLease.DisposeAsync();
        DirectoryLibraryDefinition second = await registration;

        Assert.Equal(first.RegistrationRevision + 1, second.RegistrationRevision);
        Assert.Equal("D:\\NewDocs", second.RootPath);
        Assert.Null(second.PublicationLeaseScanRunId);
    }

    [Fact]
    public async Task PackagePublicationUnderExactLeasePreservesLocalRegistrationIdentity()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DirectoryLibraryDefinition registered = await mRepository.RegisterDirectoryDefinitionAsync(
                                                    Definition("manual-library", "C:\\Docs") with
                                                        {
                                                            Name = "Local name",
                                                            Hint = "Local hint"
                                                        },
                                                    ct);
        IDirectoryPublicationLease lease = Assert.IsAssignableFrom<IDirectoryPublicationLease>(
            await mRepository.TryAcquireDirectoryPublicationLeaseAsync(
                registered.Id,
                registered.RegistrationRevision,
                registered.RegistrationIncarnationId,
                "package-import",
                expectedPublishedVersion: null,
                ct));
        var publishedAt = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        DirectoryLibraryDefinition package = Definition(registered.Id, string.Empty) with
                                                 {
                                                     Name = "Package name",
                                                     Hint = "Package hint",
                                                     Recursive = true,
                                                     AllowedExtensions = [".pdf", ".docx"],
                                                     ExclusionPatterns = ["**/archive/**"],
                                                     BindingStatus = DirectoryLibraryBindingStatus.Unbound
                                                 };

        bool applied = await mRepository.TryApplyDirectoryPackagePublicationAsync(lease,
                                                                                    expectedPublishedVersion: null,
                                                                                    package,
                                                                                    publishedAt,
                                                                                    "2026-08-08",
                                                                                    ct);
        bool staleApplied = await mRepository.TryApplyDirectoryPackagePublicationAsync(lease,
                                                                                         expectedPublishedVersion: null,
                                                                                         package,
                                                                                         publishedAt,
                                                                                         "stale",
                                                                                         ct);
        DirectoryLibraryDefinition stored = Assert.IsType<DirectoryLibraryDefinition>(
            await mRepository.GetDirectoryDefinitionAsync(registered.Id, ct));
        await lease.DisposeAsync();

        Assert.True(applied);
        Assert.False(staleApplied);
        Assert.Equal(registered.RootPath, stored.RootPath);
        Assert.Equal(registered.BindingStatus, stored.BindingStatus);
        Assert.Equal(registered.RegisteredAtUtc, stored.RegisteredAtUtc);
        Assert.Equal(registered.RegistrationRevision, stored.RegistrationRevision);
        Assert.Equal(registered.RegistrationIncarnationId, stored.RegistrationIncarnationId);
        Assert.Equal("Package name", stored.Name);
        Assert.Equal("Package hint", stored.Hint);
        Assert.True(stored.Recursive);
        Assert.Equal([".pdf", ".docx"], stored.AllowedExtensions);
        Assert.Equal(["**/archive/**"], stored.ExclusionPatterns);
        Assert.Equal("2026-08-08", stored.LastPublishedVersion);
        Assert.Equal(publishedAt, stored.LastPublishedAtUtc);
    }

    [Fact]
    public async Task PendingRenameDefinitionRejectsNormalRegistrationUpsertAndPublicationLease()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DirectoryLibraryDefinition definition = await mRepository.RegisterDirectoryDefinitionAsync(
                                                        Definition("manual-library", "C:\\Docs"),
                                                        ct);
        const string operationId = "rename-operation";
        UpdateDefinition<DirectoryLibraryDefinition> markPending =
            Builders<DirectoryLibraryDefinition>.Update.Set(item => item.PendingRenameOperationId,
                                                              operationId);
        await mContext.DirectoryLibraries.UpdateOneAsync(item => item.Id == definition.Id,
                                                         markPending,
                                                         cancellationToken: ct);

        IDirectoryPublicationLease? lease = await mRepository.TryAcquireDirectoryPublicationLeaseAsync(
                                                       definition.Id,
                                                       definition.RegistrationRevision,
                                                       definition.RegistrationIncarnationId,
                                                       "normal-scan",
                                                       expectedPublishedVersion: null,
                                                       ct);
        InvalidOperationException registrationFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mRepository.RegisterDirectoryDefinitionAsync(Definition(definition.Id, "D:\\Replacement"), ct));
        InvalidOperationException upsertFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mRepository.UpsertDirectoryDefinitionAsync(Definition(definition.Id, "D:\\Replacement"), ct));
        DirectoryLibraryDefinition stored = Assert.IsType<DirectoryLibraryDefinition>(
            await mRepository.GetDirectoryDefinitionAsync(definition.Id, ct));

        Assert.Null(lease);
        Assert.Contains("pending rename", registrationFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pending rename", upsertFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(operationId, stored.PendingRenameOperationId);
        Assert.Equal("C:\\Docs", stored.RootPath);
        Assert.Equal(definition.RegistrationRevision, stored.RegistrationRevision);
        Assert.Equal(definition.RegistrationIncarnationId, stored.RegistrationIncarnationId);
    }

    [Fact]
    public async Task PendingRenameInputCannotEnterNormalDefinitionApi()
    {
        DirectoryLibraryDefinition pending = Definition("manual-library", "C:\\Docs") with
                                                  {
                                                      PendingRenameOperationId = "rename-operation"
                                                  };

        await Assert.ThrowsAsync<ArgumentException>(() => mRepository.UpsertDirectoryDefinitionAsync(
                                                           pending,
                                                           TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => mRepository.RegisterDirectoryDefinitionAsync(
                                                           pending,
                                                           TestContext.Current.CancellationToken));
        Assert.Null(await mRepository.GetDirectoryDefinitionAsync(
                        pending.Id,
                        TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredLeaseOwnerCannotRestorePromotedMetadata()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DirectoryLibraryDefinition definition = await mRepository.RegisterDirectoryDefinitionAsync(
                                                        Definition("manual-library", "C:\\Docs"),
                                                        ct);
        IDirectoryPublicationLease? acquired = await mRepository.TryAcquireDirectoryPublicationLeaseAsync(
                                                         definition.Id,
                                                         definition.RegistrationRevision,
                                                         definition.RegistrationIncarnationId,
                                                         "publishing-scan",
                                                         expectedPublishedVersion: null,
                                                         ct);
        IDirectoryPublicationLease lease = Assert.IsAssignableFrom<IDirectoryPublicationLease>(acquired);
        var publishedAt = new DateTime(year: 2026,
                                       month: 8,
                                       day: 4,
                                       hour: 18,
                                       minute: 0,
                                       second: 0,
                                       DateTimeKind.Utc);
        Assert.True(await mRepository.TryUpdateDirectoryPublicationAsync(lease,
                                                                           expectedPublishedVersion: null,
                                                                          publishedAt,
                                                                          "2026-08-04",
                                                                          ct));
        UpdateDefinition<DirectoryLibraryDefinition> expire =
            Builders<DirectoryLibraryDefinition>.Update.Set(item => item.PublicationLeaseExpiresAtUtc,
                                                              DateTime.UtcNow.AddMinutes(-1));
        await mContext.DirectoryLibraries.UpdateOneAsync(item => item.Id == definition.Id,
                                                         expire,
                                                         cancellationToken: ct);

        bool restored = await mRepository.TryRestoreDirectoryPublicationAsync(
                            lease,
                            "2026-08-04",
                            restoredPublishedAtUtc: null,
                            restoredPublishedVersion: null,
                            ct);
        DirectoryLibraryDefinition? stored = await mRepository.GetDirectoryDefinitionAsync(definition.Id, ct);
        await lease.DisposeAsync();

        Assert.False(restored);
        Assert.NotNull(stored);
        Assert.Equal(publishedAt, stored.LastPublishedAtUtc);
        Assert.Equal("2026-08-04", stored.LastPublishedVersion);
    }

    [Fact]
    public async Task LegacyDefinitionWithoutIncarnationCanStillAcquireRenewAndReleaseLease()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        BsonDocument rawDefinition = Definition("legacy-library", "C:\\LegacyDocs").ToBsonDocument();
        rawDefinition.Remove(nameof(DirectoryLibraryDefinition.RegistrationIncarnationId));
        IMongoCollection<BsonDocument> definitions =
            mContext.Database.GetCollection<BsonDocument>("directoryLibraries");
        await definitions.InsertOneAsync(rawDefinition, cancellationToken: ct);
        DirectoryLibraryDefinition legacy = Assert.IsType<DirectoryLibraryDefinition>(
            await mRepository.GetDirectoryDefinitionAsync("legacy-library", ct));

        IDirectoryPublicationLease lease = Assert.IsAssignableFrom<IDirectoryPublicationLease>(
            await mRepository.TryAcquireDirectoryPublicationLeaseAsync(
                legacy.Id,
                legacy.RegistrationRevision,
                registrationIncarnationId: null,
                "legacy-owner",
                expectedPublishedVersion: null,
                ct));
        bool renewed = await lease.TryRenewAsync(ct);
        await lease.DisposeAsync();
        DirectoryLibraryDefinition released = Assert.IsType<DirectoryLibraryDefinition>(
            await mRepository.GetDirectoryDefinitionAsync(legacy.Id, ct));

        Assert.Null(legacy.RegistrationIncarnationId);
        Assert.True(renewed);
        Assert.Null(released.PublicationLeaseScanRunId);
        Assert.True(lease.OwnershipLostToken.IsCancellationRequested);
    }

    [Fact]
    public async Task StaleLeaseCannotDeleteDefinitionOwnedByTakeover()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DirectoryLibraryDefinition definition = await mRepository.RegisterDirectoryDefinitionAsync(
                                                        Definition("manual-library", "C:\\Docs"),
                                                        ct);
        IDirectoryPublicationLease first = Assert.IsAssignableFrom<IDirectoryPublicationLease>(
            await mRepository.TryAcquireDirectoryPublicationLeaseAsync(
                definition.Id,
                definition.RegistrationRevision,
                definition.RegistrationIncarnationId,
                "first-owner",
                expectedPublishedVersion: null,
                ct));
        UpdateDefinition<DirectoryLibraryDefinition> expire =
            Builders<DirectoryLibraryDefinition>.Update.Set(item => item.PublicationLeaseExpiresAtUtc,
                                                              DateTime.UtcNow.AddMinutes(-1));
        await mContext.DirectoryLibraries.UpdateOneAsync(item => item.Id == definition.Id,
                                                         expire,
                                                         cancellationToken: ct);
        IDirectoryPublicationLease takeover = Assert.IsAssignableFrom<IDirectoryPublicationLease>(
            await mRepository.TryAcquireDirectoryPublicationLeaseAsync(
                definition.Id,
                definition.RegistrationRevision,
                definition.RegistrationIncarnationId,
                "takeover-owner",
                expectedPublishedVersion: null,
                ct));

        bool staleDeleted = await mRepository.TryDeleteLeasedDirectoryDefinitionAsync(first, ct);
        DirectoryLibraryDefinition? afterStaleDelete = await mRepository.GetDirectoryDefinitionAsync(
                                                           definition.Id,
                                                           ct);
        bool ownerDeleted = await mRepository.TryDeleteLeasedDirectoryDefinitionAsync(takeover, ct);
        DirectoryLibraryDefinition? afterOwnerDelete = await mRepository.GetDirectoryDefinitionAsync(
                                                           definition.Id,
                                                           ct);
        await first.DisposeAsync();
        await takeover.DisposeAsync();

        Assert.False(staleDeleted);
        Assert.NotNull(afterStaleDelete);
        Assert.Equal("takeover-owner", afterStaleDelete.PublicationLeaseScanRunId);
        Assert.True(first.OwnershipLostToken.IsCancellationRequested);
        Assert.True(ownerDeleted);
        Assert.Null(afterOwnerDelete);
    }

    [Fact]
    public async Task DeletedAndRecreatedDefinitionRejectsQueuedPriorIncarnation()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DirectoryLibraryDefinition first = await mRepository.RegisterDirectoryDefinitionAsync(
                                                   Definition("manual-library", "C:\\Docs"),
                                                   ct);
        IDirectoryPublicationLease deletingLease = Assert.IsAssignableFrom<IDirectoryPublicationLease>(
            await mRepository.TryAcquireDirectoryPublicationLeaseAsync(
                first.Id,
                first.RegistrationRevision,
                first.RegistrationIncarnationId,
                "delete-owner",
                expectedPublishedVersion: null,
                ct));
        Assert.True(await mRepository.TryDeleteLeasedDirectoryDefinitionAsync(deletingLease, ct));
        await deletingLease.DisposeAsync();
        DirectoryLibraryDefinition recreated = await mRepository.RegisterDirectoryDefinitionAsync(
                                                        Definition("manual-library", "D:\\Replacement"),
                                                        ct);

        IDirectoryPublicationLease? stale = await mRepository.TryAcquireDirectoryPublicationLeaseAsync(
                                                       first.Id,
                                                       first.RegistrationRevision,
                                                       first.RegistrationIncarnationId,
                                                       "queued-old-scan",
                                                       expectedPublishedVersion: null,
                                                       ct);
        IDirectoryPublicationLease current = Assert.IsAssignableFrom<IDirectoryPublicationLease>(
            await mRepository.TryAcquireDirectoryPublicationLeaseAsync(
                recreated.Id,
                recreated.RegistrationRevision,
                recreated.RegistrationIncarnationId,
                "current-scan",
                expectedPublishedVersion: null,
                ct));
        await current.DisposeAsync();

        Assert.Equal(first.RegistrationRevision, recreated.RegistrationRevision);
        Assert.NotEqual(first.RegistrationIncarnationId, recreated.RegistrationIncarnationId);
        Assert.Null(stale);
    }

    [Fact]
    public async Task AutomaticHeartbeatKeepsLongOperationExclusivePastInitialExpiry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var heartbeatRepository = new SourceDocumentRepository(mContext,
                                                                TimeProvider.System,
                                                                TimeSpan.FromMilliseconds(750),
                                                                TimeSpan.FromMilliseconds(50));
        DirectoryLibraryDefinition definition = await heartbeatRepository.RegisterDirectoryDefinitionAsync(
                                                        Definition("heartbeat-library", "C:\\Docs"),
                                                        ct);
        IDirectoryPublicationLease lease = Assert.IsAssignableFrom<IDirectoryPublicationLease>(
            await heartbeatRepository.TryAcquireDirectoryPublicationLeaseAsync(
                definition.Id,
                definition.RegistrationRevision,
                definition.RegistrationIncarnationId,
                "long-operation",
                expectedPublishedVersion: null,
                ct));
        DirectoryLibraryDefinition initiallyLeased = Assert.IsType<DirectoryLibraryDefinition>(
            await heartbeatRepository.GetDirectoryDefinitionAsync(definition.Id, ct));
        DateTime initialExpiry = Assert.IsType<DateTime>(initiallyLeased.PublicationLeaseExpiresAtUtc);
        TimeSpan pastInitialExpiry = initialExpiry - DateTime.UtcNow + TimeSpan.FromMilliseconds(100);
        if (pastInitialExpiry > TimeSpan.Zero)
            await Task.Delay(pastInitialExpiry, ct);

        DirectoryLibraryDefinition renewedDefinition = Assert.IsType<DirectoryLibraryDefinition>(
            await heartbeatRepository.GetDirectoryDefinitionAsync(definition.Id, ct));
        IDirectoryPublicationLease? competitor = await heartbeatRepository.TryAcquireDirectoryPublicationLeaseAsync(
                                                           definition.Id,
                                                           definition.RegistrationRevision,
                                                           definition.RegistrationIncarnationId,
                                                           "competing-operation",
                                                           expectedPublishedVersion: null,
                                                           ct);
        bool ownershipMaintained = !lease.OwnershipLostToken.IsCancellationRequested;
        await lease.DisposeAsync();

        Assert.True(renewedDefinition.PublicationLeaseExpiresAtUtc > initialExpiry);
        Assert.True(ownershipMaintained);
        Assert.Null(competitor);
    }

    [Fact]
    public async Task SourceLibraryContentDeletionPreservesDirectoryDefinitionFence()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DirectoryLibraryDefinition definition = await mRepository.RegisterDirectoryDefinitionAsync(
                                                        Definition("manual-library", "C:\\Docs"),
                                                        ct);

        long deleted = await mRepository.DeleteLibraryAsync(definition.Id, ct);
        DirectoryLibraryDefinition? stored = await mRepository.GetDirectoryDefinitionAsync(definition.Id, ct);

        Assert.Equal(0, deleted);
        Assert.NotNull(stored);
        Assert.Equal(definition.RegistrationIncarnationId, stored.RegistrationIncarnationId);
    }

    [Fact]
    public async Task ArtifactsRoundTripDeduplicateAndDeleteOnlyAfterFinalReference()
    {
        var original = "shared original bytes"u8.ToArray();
        var extraction = "structured extraction"u8.ToArray();
        var firstDocument = Document("manual-library", "one.pdf", "document-one");
        var secondDocument = Document("manual-library", "two.pdf", "document-two");
        await mRepository.GetOrCreateDocumentAsync(firstDocument, TestContext.Current.CancellationToken);
        await mRepository.GetOrCreateDocumentAsync(secondDocument, TestContext.Current.CancellationToken);

        var firstRevision = Revision(firstDocument, "2026-08-04", original, extraction);
        var secondRevision = Revision(secondDocument, "2026-08-04", original);
        await PersistAsync(firstRevision, original, extraction);
        await PersistAsync(secondRevision, original);

        Assert.Equal(original, await ReadArtifactAsync(Hash(original)));
        Assert.Equal(extraction, await ReadArtifactAsync(Hash(extraction)));
        Assert.Equal(2, await mContext.DocumentArtifactBlobs.CountDocumentsAsync(FilterDefinition<DocumentArtifactBlobRecord>.Empty,
                                                                                  cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await CountGridFsFilesAsync());
        DocumentArtifactBlobRecord shared = await GetArtifactRecordAsync(Hash(original));
        Assert.Equal(1, shared.ClaimSchemaVersion);
        Assert.Equal(2, shared.Claims.Count);
        Assert.All(shared.Claims, claim => Assert.NotNull(claim.FinalizedAtUtc));

        Assert.True(await mRepository.DeleteRevisionAsync(firstRevision.Id, TestContext.Current.CancellationToken));
        Assert.Equal(original, await ReadArtifactAsync(Hash(original)));
        Assert.Single((await GetArtifactRecordAsync(Hash(original))).Claims);
        await Assert.ThrowsAsync<FileNotFoundException>(() => mRepository.OpenArtifactAsync(Hash(extraction),
                                                                                            TestContext.Current.CancellationToken));

        Assert.True(await mRepository.DeleteRevisionAsync(secondRevision.Id, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FileNotFoundException>(() => mRepository.OpenArtifactAsync(Hash(original),
                                                                                            TestContext.Current.CancellationToken));
        Assert.Equal(0, await CountGridFsFilesAsync());
    }

    [Fact]
    public async Task CandidateReplacementRemovesSupersededFinalReference()
    {
        var firstBytes = "first candidate"u8.ToArray();
        var replacementBytes = "replacement candidate"u8.ToArray();
        var document = Document("manual-library", "guide.pdf", "document-one");
        await mRepository.GetOrCreateDocumentAsync(document, TestContext.Current.CancellationToken);
        var first = Revision(document, "2026-08-04", firstBytes);
        var replacement = Revision(document, "2026-08-04", replacementBytes);

        await PersistAsync(first, firstBytes);
        await PersistAsync(replacement, replacementBytes);

        var stored = await mRepository.GetRevisionAsync(first.Id, TestContext.Current.CancellationToken);
        Assert.Equal(Hash(replacementBytes), stored?.OriginalArtifactHash);
        Assert.Equal(replacementBytes, await ReadArtifactAsync(Hash(replacementBytes)));
        await Assert.ThrowsAsync<FileNotFoundException>(() => mRepository.OpenArtifactAsync(Hash(firstBytes),
                                                                                            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RevisionWriteFailureCleansNewBlobAndPreservesSharedBlob()
    {
        var sharedBytes = "shared"u8.ToArray();
        var newBytes = "new and unreferenced"u8.ToArray();
        var sharedDocument = Document("manual-library", "shared.pdf", "shared-document");
        var conflictingDocument = Document("manual-library", "conflict.pdf", "conflicting-document");
        await mRepository.GetOrCreateDocumentAsync(sharedDocument, TestContext.Current.CancellationToken);
        await mRepository.GetOrCreateDocumentAsync(conflictingDocument, TestContext.Current.CancellationToken);
        await PersistAsync(Revision(sharedDocument, "2026-08-04", sharedBytes), sharedBytes);

        var seededConflict = Revision(conflictingDocument, "2026-08-04", sharedBytes) with { Id = "non-canonical-conflict" };
        await mContext.DocumentRevisions.InsertOneAsync(seededConflict,
                                                        cancellationToken: TestContext.Current.CancellationToken);
        var attempted = Revision(conflictingDocument, "2026-08-04", newBytes) with
                            {
                                ExtractionArtifactHash = Hash(sharedBytes),
                                ExtractionByteLength = sharedBytes.Length,
                                ExtractionMediaType = "application/json"
                            };

        await using var originalStream = new MemoryStream(newBytes, writable: false);
        await using var extractionStream = new MemoryStream(sharedBytes, writable: false);
        await Assert.ThrowsAnyAsync<MongoException>(() => mRepository.PersistRevisionAsync(
                                                        attempted,
                                                        originalStream,
                                                        extractionStream,
                                                        TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<FileNotFoundException>(() => mRepository.OpenArtifactAsync(Hash(newBytes),
                                                                                            TestContext.Current.CancellationToken));
        Assert.Equal(sharedBytes, await ReadArtifactAsync(Hash(sharedBytes)));
        Assert.Equal(1, await CountGridFsFilesAsync());
    }

    [Fact]
    public async Task NonSeekableHashMismatchRejectsRevisionAndDeletesUploadedBytes()
    {
        var declaredBytes = "declared"u8.ToArray();
        var differentBytes = "tampered"u8.ToArray();
        var document = Document("manual-library", "mismatch.pdf", "mismatch-document");
        await mRepository.GetOrCreateDocumentAsync(document, TestContext.Current.CancellationToken);
        var revision = Revision(document, "2026-08-04", declaredBytes);
        await using var source = new NonSeekableReadStream(new MemoryStream(differentBytes, writable: false));

        await Assert.ThrowsAsync<InvalidDataException>(() => mRepository.PersistRevisionAsync(
                                                            revision,
                                                            source,
                                                            extractionArtifact: null,
                                                            TestContext.Current.CancellationToken));

        Assert.Null(await mRepository.GetRevisionAsync(revision.Id, TestContext.Current.CancellationToken));
        Assert.Equal(0,
                     await mContext.DocumentArtifactBlobs.CountDocumentsAsync(
                         FilterDefinition<DocumentArtifactBlobRecord>.Empty,
                         cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, await CountGridFsFilesAsync());
    }

    [Fact]
    public async Task PublicationRacingCandidateReplacementWinsAndCleansLosingArtifact()
    {
        var publishedBytes = "published candidate"u8.ToArray();
        var replacementBytes = "losing replacement"u8.ToArray();
        var document = Document("manual-library", "race.pdf", "race-document");
        await mRepository.GetOrCreateDocumentAsync(document, TestContext.Current.CancellationToken);
        var published = Revision(document, "2026-08-04", publishedBytes);
        await PersistAsync(published, publishedBytes);
        var replacement = Revision(document, "2026-08-04", replacementBytes);
        await using var source = new BlockingReadStream(new MemoryStream(replacementBytes, writable: false));

        var replacementWrite = Task.Run(() => mRepository.PersistRevisionAsync(
                                            replacement,
                                            source,
                                            extractionArtifact: null,
                                            TestContext.Current.CancellationToken),
                                        TestContext.Current.CancellationToken);
        await source.WaitForReadAsync(TestContext.Current.CancellationToken);
        try
        {
            var update = Builders<DocumentRevisionRecord>.Update.Set(r => r.State,
                                                                      DocumentRevisionState.Published);
            await mContext.DocumentRevisions.UpdateOneAsync(r => r.Id == published.Id,
                                                            update,
                                                            cancellationToken:
                                                            TestContext.Current.CancellationToken);
        }
        finally
        {
            source.Release();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => replacementWrite);

        var stored = await mRepository.GetRevisionAsync(published.Id, TestContext.Current.CancellationToken);
        Assert.Equal(DocumentRevisionState.Published, stored?.State);
        Assert.Equal(Hash(publishedBytes), stored?.OriginalArtifactHash);
        Assert.Equal(publishedBytes, await ReadArtifactAsync(Hash(publishedBytes)));
        await Assert.ThrowsAsync<FileNotFoundException>(() => mRepository.OpenArtifactAsync(
                                                            Hash(replacementBytes),
                                                            TestContext.Current.CancellationToken));
        Assert.Equal(1, await CountGridFsFilesAsync());
    }

    [Fact]
    public async Task ConcurrentPreparedClaimPreventsDeletionUntilRecoveryExpiresIt()
    {
        var bytes = "claim-protected artifact"u8.ToArray();
        var document = Document("manual-library", "claimed.pdf", "claimed-document");
        await mRepository.GetOrCreateDocumentAsync(document, TestContext.Current.CancellationToken);
        DocumentRevisionRecord revision = Revision(document, "2026-08-04", bytes);
        await PersistAsync(revision, bytes);
        string hash = Hash(bytes);
        DateTime preparedAtUtc = DateTime.UtcNow;
        var competingClaim = new DocumentArtifactClaimRecord
                                 {
                                     ClaimId = $"competing:{hash}",
                                     RevisionId = "revision-being-prepared",
                                     PreparedAtUtc = preparedAtUtc,
                                     ExpiresAtUtc = preparedAtUtc.AddMinutes(5)
                                 };
        UpdateDefinition<DocumentArtifactBlobRecord> addClaim =
            Builders<DocumentArtifactBlobRecord>.Update.Push(artifact => artifact.Claims, competingClaim);
        await mContext.DocumentArtifactBlobs.UpdateOneAsync(artifact => artifact.Id == hash,
                                                            addClaim,
                                                            cancellationToken:
                                                            TestContext.Current.CancellationToken);

        Assert.True(await mRepository.DeleteRevisionAsync(revision.Id,
                                                           TestContext.Current.CancellationToken));
        Assert.Equal(bytes, await ReadArtifactAsync(hash));
        DocumentArtifactRecoveryResult early = await mRepository.RecoverArtifactClaimsAsync(
                                                   preparedAtUtc.AddMinutes(1),
                                                   TestContext.Current.CancellationToken);
        Assert.Equal(new DocumentArtifactRecoveryResult(0, 0, 0), early);

        DocumentArtifactRecoveryResult expired = await mRepository.RecoverArtifactClaimsAsync(
                                                     preparedAtUtc.AddMinutes(6),
                                                     TestContext.Current.CancellationToken);
        Assert.Equal(new DocumentArtifactRecoveryResult(0, 1, 1), expired);
        await Assert.ThrowsAsync<FileNotFoundException>(() => mRepository.OpenArtifactAsync(
                                                            hash,
                                                            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecoveryFinalizesCommittedPreparedClaim()
    {
        var bytes = "prepared committed artifact"u8.ToArray();
        var document = Document("manual-library", "prepared.pdf", "prepared-document");
        await mRepository.GetOrCreateDocumentAsync(document, TestContext.Current.CancellationToken);
        DocumentRevisionRecord revision = Revision(document, "2026-08-04", bytes);
        await PersistAsync(revision, bytes);
        string hash = Hash(bytes);
        DateTime recoveryAtUtc = DateTime.UtcNow;
        UpdateDefinition<DocumentArtifactBlobRecord> prepare =
            Builders<DocumentArtifactBlobRecord>.Update
                                                    .Set(FirstClaimFinalizedAtFieldPath, (DateTime?)null)
                                                    .Set(FirstClaimExpiresAtFieldPath,
                                                         recoveryAtUtc.AddMinutes(5));
        await mContext.DocumentArtifactBlobs.UpdateOneAsync(artifact => artifact.Id == hash,
                                                            prepare,
                                                            cancellationToken:
                                                            TestContext.Current.CancellationToken);

        DocumentArtifactRecoveryResult result = await mRepository.RecoverArtifactClaimsAsync(
                                                   recoveryAtUtc,
                                                   TestContext.Current.CancellationToken);

        Assert.Equal(new DocumentArtifactRecoveryResult(1, 0, 0), result);
        DocumentArtifactClaimRecord claim = Assert.Single((await GetArtifactRecordAsync(hash)).Claims);
        Assert.NotNull(claim.FinalizedAtUtc);
        Assert.Null(claim.ExpiresAtUtc);
    }

    [Fact]
    public async Task ExpiredPreparedClaimCannotBeRenewed()
    {
        var bytes = "expired prepared claim"u8.ToArray();
        string hash = Hash(bytes);
        DateTime renewalAtUtc = DateTime.UtcNow;
        var expiredClaim = new DocumentArtifactClaimRecord
                               {
                                   ClaimId = "expired-claim",
                                   RevisionId = "expired-revision",
                                   PreparedAtUtc = renewalAtUtc.AddMinutes(-20),
                                   ExpiresAtUtc = renewalAtUtc.AddMinutes(-1)
                               };
        await InsertArtifactRecordAsync(bytes, claimSchemaVersion: 1, claims: [expiredClaim]);

        bool renewed = await mRepository.TryRenewPreparedArtifactClaimAsync(
                           hash,
                           expiredClaim.RevisionId,
                           expiredClaim.ClaimId,
                           renewalAtUtc,
                           renewalAtUtc.AddMinutes(15),
                           TestContext.Current.CancellationToken);

        Assert.False(renewed);
        DocumentArtifactClaimRecord stored = Assert.Single((await GetArtifactRecordAsync(hash)).Claims);
        Assert.NotNull(expiredClaim.ExpiresAtUtc);
        Assert.NotNull(stored.ExpiresAtUtc);
        Assert.Equal(expiredClaim.ExpiresAtUtc.Value.Ticks / TimeSpan.TicksPerMillisecond,
                     stored.ExpiresAtUtc.Value.Ticks / TimeSpan.TicksPerMillisecond);
    }

    [Fact]
    public async Task RecoveryCannotReleaseClaimRenewedAfterItsSnapshot()
    {
        var bytes = "renewed after recovery snapshot"u8.ToArray();
        string hash = Hash(bytes);
        DateTime observedAtUtc = DateTime.UtcNow;
        var observedClaim = new DocumentArtifactClaimRecord
                                {
                                    ClaimId = "observed-expired-claim",
                                    RevisionId = "observed-revision",
                                    PreparedAtUtc = observedAtUtc.AddMinutes(-20),
                                    ExpiresAtUtc = observedAtUtc.AddMinutes(-1)
                                };
        await InsertArtifactRecordAsync(bytes, claimSchemaVersion: 1, claims: [observedClaim]);
        DateTime renewedExpiresAtUtc = observedAtUtc.AddMinutes(15);
        UpdateDefinition<DocumentArtifactBlobRecord> renew =
            Builders<DocumentArtifactBlobRecord>.Update.Set(FirstClaimExpiresAtFieldPath,
                                                              renewedExpiresAtUtc);
        await mContext.DocumentArtifactBlobs.UpdateOneAsync(artifact => artifact.Id == hash,
                                                            renew,
                                                            cancellationToken:
                                                            TestContext.Current.CancellationToken);

        bool released = await mRepository.TryReleaseObservedArtifactClaimAsync(
                            hash,
                            observedClaim,
                            TestContext.Current.CancellationToken);

        Assert.False(released);
        DocumentArtifactClaimRecord stored = Assert.Single((await GetArtifactRecordAsync(hash)).Claims);
        Assert.NotNull(stored.ExpiresAtUtc);
        Assert.Equal(renewedExpiresAtUtc.Ticks / TimeSpan.TicksPerMillisecond,
                     stored.ExpiresAtUtc.Value.Ticks / TimeSpan.TicksPerMillisecond);
        Assert.Equal(bytes, await ReadArtifactAsync(hash));
    }

    [Fact]
    public async Task AmbiguousUploadReconciliationPreservesConcurrentAdopter()
    {
        var bytes = "concurrently adopted upload"u8.ToArray();
        string hash = Hash(bytes);
        DateTime preparedAtUtc = DateTime.UtcNow;
        var initiatingClaim = new DocumentArtifactClaimRecord
                                  {
                                      ClaimId = "initiating-claim",
                                      RevisionId = "initiating-revision",
                                      PreparedAtUtc = preparedAtUtc,
                                      ExpiresAtUtc = preparedAtUtc.AddMinutes(15)
                                  };
        var adoptingClaim = new DocumentArtifactClaimRecord
                                {
                                    ClaimId = "adopting-claim",
                                    RevisionId = "adopting-revision",
                                    PreparedAtUtc = preparedAtUtc,
                                    ExpiresAtUtc = preparedAtUtc.AddMinutes(15)
                                };
        await InsertArtifactRecordAsync(bytes,
                                        claimSchemaVersion: 1,
                                        claims: [initiatingClaim, adoptingClaim]);
        DocumentArtifactBlobRecord stored = await GetArtifactRecordAsync(hash);
        DocumentArtifactBlobRecord ambiguousInsert = stored with { Claims = [initiatingClaim] };

        await mRepository.ReconcileAmbiguousUploadAsync(ambiguousInsert,
                                                        TestContext.Current.CancellationToken);

        DocumentArtifactClaimRecord remaining = Assert.Single((await GetArtifactRecordAsync(hash)).Claims);
        Assert.Equal(adoptingClaim.ClaimId, remaining.ClaimId);
        Assert.Equal(bytes, await ReadArtifactAsync(hash));
        Assert.Equal(1, await CountGridFsFilesAsync());
    }

    [Fact]
    public async Task AmbiguousUploadWithoutVisibleMetadataRetainsPrivateBytes()
    {
        var bytes = "ambiguous upload with no visible metadata"u8.ToArray();
        string hash = Hash(bytes);
        await using var stream = new MemoryStream(bytes, writable: false);
        ObjectId fileId = await mContext.DocumentArtifactsBucket.UploadFromStreamAsync(
                              SourceDocumentRepository.MakeArtifactFilename(hash),
                              stream,
                              cancellationToken: TestContext.Current.CancellationToken);
        DateTime preparedAtUtc = DateTime.UtcNow;
        var ambiguousInsert = new DocumentArtifactBlobRecord
                                  {
                                      Id = hash,
                                      GridFsId = fileId.ToString(),
                                      ByteLength = bytes.LongLength,
                                      CreatedAtUtc = preparedAtUtc,
                                      ClaimSchemaVersion = 1,
                                      Claims =
                                      [
                                          new DocumentArtifactClaimRecord
                                              {
                                                  ClaimId = "ambiguous-claim",
                                                  RevisionId = "ambiguous-revision",
                                                  PreparedAtUtc = preparedAtUtc,
                                                  ExpiresAtUtc = preparedAtUtc.AddMinutes(15)
                                              }
                                      ]
                                  };

        await mRepository.ReconcileAmbiguousUploadAsync(ambiguousInsert,
                                                        TestContext.Current.CancellationToken);

        Assert.Equal(0,
                     await mContext.DocumentArtifactBlobs.CountDocumentsAsync(
                         artifact => artifact.Id == hash,
                         cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await CountGridFsFilesAsync());
    }

    [Fact]
    public async Task AmbiguousUploadReconciliationDeletesOnlyKnownPrivateLoser()
    {
        var bytes = "known private losing upload"u8.ToArray();
        string hash = Hash(bytes);
        DateTime preparedAtUtc = DateTime.UtcNow;
        var winningClaim = new DocumentArtifactClaimRecord
                               {
                                   ClaimId = "winning-claim",
                                   RevisionId = "winning-revision",
                                   PreparedAtUtc = preparedAtUtc,
                                   ExpiresAtUtc = preparedAtUtc.AddMinutes(15)
                               };
        await InsertArtifactRecordAsync(bytes, claimSchemaVersion: 1, claims: [winningClaim]);
        DocumentArtifactBlobRecord winner = await GetArtifactRecordAsync(hash);
        await using var privateStream = new MemoryStream(bytes, writable: false);
        ObjectId privateFileId = await mContext.DocumentArtifactsBucket.UploadFromStreamAsync(
                                     SourceDocumentRepository.MakeArtifactFilename(hash),
                                     privateStream,
                                     cancellationToken: TestContext.Current.CancellationToken);
        var losingUpload = new DocumentArtifactBlobRecord
                               {
                                   Id = hash,
                                   GridFsId = privateFileId.ToString(),
                                   ByteLength = bytes.LongLength,
                                   CreatedAtUtc = preparedAtUtc,
                                   ClaimSchemaVersion = 1,
                                   Claims =
                                   [
                                       new DocumentArtifactClaimRecord
                                           {
                                               ClaimId = "losing-claim",
                                               RevisionId = "losing-revision",
                                               PreparedAtUtc = preparedAtUtc,
                                               ExpiresAtUtc = preparedAtUtc.AddMinutes(15)
                                           }
                                   ]
                               };

        await mRepository.ReconcileAmbiguousUploadAsync(losingUpload,
                                                        TestContext.Current.CancellationToken);

        DocumentArtifactBlobRecord stored = await GetArtifactRecordAsync(hash);
        Assert.Equal(winner.GridFsId, stored.GridFsId);
        Assert.Equal(winningClaim.ClaimId, Assert.Single(stored.Claims).ClaimId);
        Assert.Equal(bytes, await ReadArtifactAsync(hash));
        Assert.Equal(1, await CountGridFsFilesAsync());
    }

    [Fact]
    public async Task LegacyBlobWithoutClaimSchemaIsNeverDeletionEligible()
    {
        var bytes = "legacy artifact bytes"u8.ToArray();
        string hash = Hash(bytes);
        await InsertArtifactRecordAsync(bytes, claimSchemaVersion: null, claims: []);
        var document = Document("manual-library", "legacy.pdf", "legacy-document");
        await mRepository.GetOrCreateDocumentAsync(document, TestContext.Current.CancellationToken);
        DocumentRevisionRecord revision = Revision(document, "2026-08-04", bytes);

        await PersistAsync(revision, bytes);
        DocumentRevisionRecord stored = Assert.IsType<DocumentRevisionRecord>(
            await mRepository.GetRevisionAsync(revision.Id, TestContext.Current.CancellationToken));
        Assert.Empty(stored.ArtifactClaims);
        Assert.True(await mRepository.DeleteRevisionAsync(revision.Id,
                                                           TestContext.Current.CancellationToken));

        Assert.Equal(bytes, await ReadArtifactAsync(hash));
        Assert.Null((await GetArtifactRecordAsync(hash)).ClaimSchemaVersion);
        Assert.Equal(1, await CountGridFsFilesAsync());
    }

    [Fact]
    public async Task RawLegacyBlobWithMissingClaimFieldsRoundTripsAndRemainsProtected()
    {
        var bytes = "raw legacy artifact bytes"u8.ToArray();
        string hash = Hash(bytes);
        await using var stream = new MemoryStream(bytes, writable: false);
        ObjectId fileId = await mContext.DocumentArtifactsBucket.UploadFromStreamAsync(
                              SourceDocumentRepository.MakeArtifactFilename(hash),
                              stream,
                              cancellationToken: TestContext.Current.CancellationToken);
        IMongoCollection<BsonDocument> artifacts =
            mContext.Database.GetCollection<BsonDocument>("documentArtifactBlobs");
        await artifacts.InsertOneAsync(new BsonDocument
                                           {
                                               ["_id"] = hash,
                                               ["GridFsId"] = fileId.ToString(),
                                               ["ByteLength"] = bytes.LongLength,
                                               ["CreatedAtUtc"] = DateTime.UtcNow
                                           },
                                       cancellationToken: TestContext.Current.CancellationToken);
        var document = Document("manual-library", "raw-legacy.pdf", "raw-legacy-document");
        await mRepository.GetOrCreateDocumentAsync(document, TestContext.Current.CancellationToken);
        DocumentRevisionRecord revision = Revision(document, "2026-08-04", bytes);

        await PersistAsync(revision, bytes);
        Assert.True(await mRepository.DeleteRevisionAsync(revision.Id,
                                                           TestContext.Current.CancellationToken));

        Assert.Equal(bytes, await ReadArtifactAsync(hash));
        DocumentArtifactBlobRecord stored = await GetArtifactRecordAsync(hash);
        Assert.Null(stored.ClaimSchemaVersion);
        Assert.Empty(stored.Claims);
        Assert.Equal(1, await CountGridFsFilesAsync());
    }

    [Fact]
    public async Task RawLegacyRevisionWithMissingArtifactClaimsDeletesWithoutTouchingLegacyBlob()
    {
        var bytes = "raw legacy revision bytes"u8.ToArray();
        string hash = Hash(bytes);
        await InsertArtifactRecordAsync(bytes, claimSchemaVersion: null, claims: []);
        var document = Document("manual-library", "raw-revision.pdf", "raw-revision-document");
        await mRepository.GetOrCreateDocumentAsync(document, TestContext.Current.CancellationToken);
        DocumentRevisionRecord revision = Revision(document, "2026-08-04", bytes);
        BsonDocument rawRevision = revision.ToBsonDocument();
        rawRevision.Remove(nameof(DocumentRevisionRecord.ArtifactClaims));
        IMongoCollection<BsonDocument> revisions =
            mContext.Database.GetCollection<BsonDocument>("documentRevisions");
        await revisions.InsertOneAsync(rawRevision,
                                       cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await mRepository.DeleteRevisionAsync(revision.Id,
                                                           TestContext.Current.CancellationToken));

        Assert.Equal(bytes, await ReadArtifactAsync(hash));
        Assert.Null((await GetArtifactRecordAsync(hash)).ClaimSchemaVersion);
        Assert.Equal(1, await CountGridFsFilesAsync());
    }

    [Fact]
    public async Task UnknownClaimSchemaIsNeverMutated()
    {
        var bytes = "future artifact schema"u8.ToArray();
        string hash = Hash(bytes);
        await InsertArtifactRecordAsync(bytes, claimSchemaVersion: 99, claims: []);
        var document = Document("manual-library", "future.pdf", "future-document");
        await mRepository.GetOrCreateDocumentAsync(document, TestContext.Current.CancellationToken);
        DocumentRevisionRecord revision = Revision(document, "2026-08-04", bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => PersistAsync(revision, bytes));

        Assert.Null(await mRepository.GetRevisionAsync(revision.Id, TestContext.Current.CancellationToken));
        DocumentArtifactBlobRecord stored = await GetArtifactRecordAsync(hash);
        Assert.Equal(99, stored.ClaimSchemaVersion);
        Assert.Empty(stored.Claims);
        Assert.Equal(bytes, await ReadArtifactAsync(hash));
    }

    [Fact]
    public async Task RecoveryCompletesTombstoneWhenGridFsFileIsAlreadyMissing()
    {
        var bytes = "already deleted artifact"u8.ToArray();
        string hash = Hash(bytes);
        var tombstone = new DocumentArtifactBlobRecord
                            {
                                Id = hash,
                                GridFsId = ObjectId.GenerateNewId().ToString(),
                                ByteLength = bytes.LongLength,
                                CreatedAtUtc = DateTime.UtcNow,
                                ClaimSchemaVersion = 1,
                                Claims = [],
                                DeletionId = Guid.NewGuid().ToString("N"),
                                DeletionPreparedAtUtc = DateTime.UtcNow
                            };
        await mContext.DocumentArtifactBlobs.InsertOneAsync(tombstone,
                                                            cancellationToken:
                                                            TestContext.Current.CancellationToken);

        DocumentArtifactRecoveryResult result = await mRepository.RecoverArtifactClaimsAsync(
                                                   DateTime.UtcNow,
                                                   TestContext.Current.CancellationToken);

        Assert.Equal(new DocumentArtifactRecoveryResult(0, 0, 1), result);
        Assert.Equal(0,
                     await mContext.DocumentArtifactBlobs.CountDocumentsAsync(
                         artifact => artifact.Id == hash,
                         cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task PersistAsync(DocumentRevisionRecord revision, byte[] original, byte[]? extraction = null)
    {
        await using var originalStream = new MemoryStream(original, writable: false);
        await using var extractionStream = extraction == null ? null : new MemoryStream(extraction, writable: false);
        await mRepository.PersistRevisionAsync(revision,
                                               originalStream,
                                               extractionStream,
                                               TestContext.Current.CancellationToken);
    }

    private async Task<byte[]> ReadArtifactAsync(string hash)
    {
        await using var stream = await mRepository.OpenArtifactAsync(hash, TestContext.Current.CancellationToken);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
        return copy.ToArray();
    }

    private async Task<long> CountGridFsFilesAsync()
    {
        var files = mContext.Database.GetCollection<BsonDocument>("documentArtifacts.files");
        return await files.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
                                                cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<DocumentArtifactBlobRecord> GetArtifactRecordAsync(string hash)
    {
        DocumentArtifactBlobRecord? result = await mContext.DocumentArtifactBlobs
                                                           .Find(artifact => artifact.Id == hash)
                                                           .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        return Assert.IsType<DocumentArtifactBlobRecord>(result);
    }

    private async Task InsertArtifactRecordAsync(byte[] bytes,
                                                 int? claimSchemaVersion,
                                                 IReadOnlyList<DocumentArtifactClaimRecord> claims)
    {
        string hash = Hash(bytes);
        await using var stream = new MemoryStream(bytes, writable: false);
        ObjectId fileId = await mContext.DocumentArtifactsBucket.UploadFromStreamAsync(
                              SourceDocumentRepository.MakeArtifactFilename(hash),
                              stream,
                              cancellationToken: TestContext.Current.CancellationToken);
        var record = new DocumentArtifactBlobRecord
                         {
                             Id = hash,
                             GridFsId = fileId.ToString(),
                             ByteLength = bytes.LongLength,
                             CreatedAtUtc = DateTime.UtcNow,
                             ClaimSchemaVersion = claimSchemaVersion,
                             Claims = claims
                         };
        await mContext.DocumentArtifactBlobs.InsertOneAsync(record,
                                                            cancellationToken:
                                                            TestContext.Current.CancellationToken);
    }

    private static DirectoryLibraryDefinition Definition(string libraryId, string rootPath) => new()
        {
            Id = libraryId,
            RootPath = rootPath,
            RegisteredAtUtc = new DateTime(year: 2026,
                                           month: 8,
                                           day: 4,
                                           hour: 12,
                                           minute: 0,
                                           second: 0,
                                           DateTimeKind.Utc)
        };

    private static SourceDocumentRecord Document(string libraryId, string relativePath, string id) => new()
        {
            Id = id,
            LibraryId = libraryId,
            NormalizedRelativePath = relativePath,
            DisplayRelativePath = relativePath,
            DisplayName = Path.GetFileName(relativePath),
            SourceUri = $"saddlerag://library/{libraryId}/documents/{id}",
            MediaType = "application/pdf",
            FirstSeenVersion = "2026-08-04",
            CreatedAtUtc = DateTime.UtcNow
        };

    private static DocumentRevisionRecord Revision(SourceDocumentRecord document,
                                                   string version,
                                                   byte[] original,
                                                   byte[]? extraction = null) => new()
        {
            Id = SourceDocumentRepository.MakeRevisionId(document.LibraryId, version, document.Id),
            DocumentId = document.Id,
            LibraryId = document.LibraryId,
            Version = version,
            ScanRunId = "scan-run",
            State = DocumentRevisionState.Candidate,
            SourceModifiedAtUtc = DateTime.UtcNow,
            AcquiredAtUtc = DateTime.UtcNow,
            OriginalArtifactHash = Hash(original),
            OriginalByteLength = original.Length,
            OriginalMediaType = document.MediaType,
            ExtractionArtifactHash = extraction == null ? null : Hash(extraction),
            ExtractionByteLength = extraction?.Length,
            ExtractionMediaType = extraction == null ? null : "application/json"
        };

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private const string FirstClaimExpiresAtFieldPath = "Claims.0.ExpiresAtUtc";
    private const string FirstClaimFinalizedAtFieldPath = "Claims.0.FinalizedAtUtc";
    private const string TestConnectionString = "mongodb://localhost:27017";
}
