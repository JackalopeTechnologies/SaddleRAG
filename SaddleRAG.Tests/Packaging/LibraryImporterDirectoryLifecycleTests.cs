// LibraryImporterDirectoryLifecycleTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Packaging;
using SaddleRAG.Tests.Packaging.Fixtures;

namespace SaddleRAG.Tests.Packaging;

public sealed class LibraryImporterDirectoryLifecycleTests : IAsyncLifetime
{
    private readonly List<string> mBundlePaths = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        foreach(string path in mBundlePaths.Where(File.Exists))
            File.Delete(path);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ZeroVersionDirectoryBundleIsRejectedBeforeModeAcquisition()
    {
        string bundle = CreateManifestOnlyBundle([]);
        ImportFixture fixture = Fixture(existingDefinition: null);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Importer.ImportAsync(
                                                           new ImportRequest { BundlePath = bundle },
                                                           progress: null,
                                                           TestContext.Current.CancellationToken));

        await fixture.ModeManager.DidNotReceiveWithAnyArgs()
                     .TryAcquireAsync(default,
                                      default!,
                                      default,
                                      TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NullDirectoryOptionListIsRejectedBeforeModeAcquisition(bool nullExtensions)
    {
        string bundle = CreateValidBundle(nullExtensions: nullExtensions,
                                          nullExclusions: !nullExtensions);
        ImportFixture fixture = Fixture(existingDefinition: null);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Importer.ImportAsync(
                                                           new ImportRequest { BundlePath = bundle },
                                                           progress: null,
                                                           TestContext.Current.CancellationToken));

        await fixture.ModeManager.DidNotReceiveWithAnyArgs()
                     .TryAcquireAsync(default,
                                      default!,
                                      default,
                                      TestContext.Current.CancellationToken);
        await AssertNoPurgeOrWritesAsync(fixture);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("../malformed")]
    public async Task DuplicateOrMalformedVersionIdIsRejectedBeforeModeAcquisition(string caseValue)
    {
        IReadOnlyList<string> versions = caseValue == "duplicate" ? [Version, Version] : [caseValue];
        string bundle = CreateManifestOnlyBundle(versions);
        ImportFixture fixture = Fixture(existingDefinition: null);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Importer.ImportAsync(
                                                           new ImportRequest { BundlePath = bundle },
                                                           progress: null,
                                                           TestContext.Current.CancellationToken));

        await fixture.ModeManager.DidNotReceiveWithAnyArgs()
                      .TryAcquireAsync(default,
                                       default!,
                                       default,
                                       TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(PackageRecordType.SourceDocument, InvalidIdentityPart.Library)]
    [InlineData(PackageRecordType.SourceDocument, InvalidIdentityPart.Id)]
    [InlineData(PackageRecordType.LibraryVersion, InvalidIdentityPart.Library)]
    [InlineData(PackageRecordType.LibraryVersion, InvalidIdentityPart.Version)]
    [InlineData(PackageRecordType.LibraryVersion, InvalidIdentityPart.Id)]
    [InlineData(PackageRecordType.Profile, InvalidIdentityPart.Library)]
    [InlineData(PackageRecordType.Profile, InvalidIdentityPart.Version)]
    [InlineData(PackageRecordType.Profile, InvalidIdentityPart.Id)]
    [InlineData(PackageRecordType.Index, InvalidIdentityPart.Library)]
    [InlineData(PackageRecordType.Index, InvalidIdentityPart.Version)]
    [InlineData(PackageRecordType.Index, InvalidIdentityPart.Id)]
    [InlineData(PackageRecordType.Diff, InvalidIdentityPart.Library)]
    [InlineData(PackageRecordType.Diff, InvalidIdentityPart.Version)]
    [InlineData(PackageRecordType.Diff, InvalidIdentityPart.Id)]
    [InlineData(PackageRecordType.ExcludedSymbol, InvalidIdentityPart.Library)]
    [InlineData(PackageRecordType.ExcludedSymbol, InvalidIdentityPart.Version)]
    [InlineData(PackageRecordType.ExcludedSymbol, InvalidIdentityPart.Id)]
    [InlineData(PackageRecordType.Page, InvalidIdentityPart.Library)]
    [InlineData(PackageRecordType.Page, InvalidIdentityPart.Version)]
    [InlineData(PackageRecordType.Page, InvalidIdentityPart.Id)]
    [InlineData(PackageRecordType.Chunk, InvalidIdentityPart.Library)]
    [InlineData(PackageRecordType.Chunk, InvalidIdentityPart.Version)]
    [InlineData(PackageRecordType.Chunk, InvalidIdentityPart.Id)]
    public async Task InvalidPackageRecordIsRejectedBeforePurgeOrWrites(PackageRecordType recordType,
                                                                        InvalidIdentityPart identityPart)
    {
        string bundle = CreateInvalidScopedRecordBundle(recordType, identityPart);
        ImportFixture fixture = WebFixture();

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Importer.ImportAsync(
                                                           new ImportRequest
                                                               {
                                                                   BundlePath = bundle,
                                                                   Overwrite = true
                                                               },
                                                           progress: null,
                                                           TestContext.Current.CancellationToken));

        await AssertNoPurgeOrWritesAsync(fixture);
    }

    [Theory]
    [InlineData(Bm25Corruption.InlineForeignChunk)]
    [InlineData(Bm25Corruption.ExternalForeignChunk)]
    [InlineData(Bm25Corruption.ExternalMalformedPayload)]
    [InlineData(Bm25Corruption.WholeShardForeignChunk)]
    [InlineData(Bm25Corruption.WrongShardRouting)]
    [InlineData(Bm25Corruption.DocumentCountMismatch)]
    public async Task InvalidBm25PackageIsRejectedBeforePurgeOrWrites(Bm25Corruption corruption)
    {
        string bundle = CreateBm25Bundle(corruption);
        ImportFixture fixture = WebFixture();

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Importer.ImportAsync(
                                                           new ImportRequest
                                                               {
                                                                   BundlePath = bundle,
                                                                   Overwrite = true
                                                               },
                                                           progress: null,
                                                           TestContext.Current.CancellationToken));

        await AssertNoPurgeOrWritesAsync(fixture);
    }

    [Fact]
    public async Task ValidGridFsBm25BundleUsesCallerGeneratedIdsAndRewritesEveryReference()
    {
        string bundle = CreateBm25Bundle(Bm25Corruption.ValidExternalPayload);
        ImportFixture fixture = NewWebFixture();
        var uploadedPayloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var persistedShards = new List<Bm25Shard>();
        fixture.Bm25.UploadGridFsBlobAsync(Arg.Any<string>(),
                                           Arg.Any<Stream>(),
                                           Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                string gridFsId = call.ArgAt<string>(0);
                                using var buffer = new MemoryStream();
                                call.ArgAt<Stream>(1).CopyTo(buffer);
                                uploadedPayloads.Add(gridFsId, buffer.ToArray());
                                return Task.CompletedTask;
                            });
        fixture.Bm25.UpsertShardAsync(Arg.Any<Bm25Shard>(), Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                persistedShards.Add(call.ArgAt<Bm25Shard>(0));
                                return Task.CompletedTask;
                            });

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.Equal([Version], result.VersionsImported);
        Assert.NotEmpty(uploadedPayloads);
        Assert.All(uploadedPayloads.Keys, gridFsId => Assert.True(ObjectId.TryParse(gridFsId, out _)));
        IReadOnlySet<string> rewrittenReferences = persistedShards
            .SelectMany(shard => shard.ShardGridFsRef is { } wholeShardReference
                                     ? shard.ExternalTerms.Values.Append(wholeShardReference)
                                     : shard.ExternalTerms.Values)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(uploadedPayloads.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(rewrittenReferences));
    }

    [Fact]
    public async Task LaterBm25ShardFailureDeletesEveryLoggedCallerGeneratedBlob()
    {
        string bundle = CreateBm25Bundle(Bm25Corruption.ValidExternalPayload);
        ImportFixture fixture = ExistingWebFixture(PreviousLibrary());
        var uploadedIds = new List<string>();
        fixture.Bm25.UploadGridFsBlobAsync(Arg.Any<string>(),
                                           Arg.Any<Stream>(),
                                           Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                uploadedIds.Add(call.ArgAt<string>(0));
                                return Task.CompletedTask;
                            });
        fixture.Bm25.UpsertShardAsync(Arg.Any<Bm25Shard>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException(new IOException("forced shard failure")));

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.Single(result.PartialFailures);
        Assert.NotEmpty(uploadedIds);
        foreach(string gridFsId in uploadedIds)
        {
            await fixture.Bm25.Received()
                         .DeleteGridFsBlobAsync(gridFsId, Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task AppliedThenThrowBm25UploadDeletesItsCallerGeneratedBlob()
    {
        string bundle = CreateBm25Bundle(Bm25Corruption.ValidExternalPayload);
        ImportFixture fixture = ExistingWebFixture(PreviousLibrary());
        string? attemptedGridFsId = null;
        fixture.Bm25.UploadGridFsBlobAsync(Arg.Any<string>(),
                                           Arg.Any<Stream>(),
                                           Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                attemptedGridFsId = call.ArgAt<string>(0);
                                return Task.FromException(new IOException("upload acknowledgement lost"));
                            });

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.Single(result.PartialFailures);
        string gridFsId = Assert.IsType<string>(attemptedGridFsId);
        await fixture.Bm25.Received(requiredNumberOfCalls: 1)
                     .DeleteGridFsBlobAsync(gridFsId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetainedVersionAfterBm25FailurePreservesLoggedGridFsBlobs()
    {
        string bundle = CreateBm25Bundle(Bm25Corruption.ValidExternalPayload);
        ImportFixture fixture = ExistingWebFixture(PreviousLibrary());
        var uploadedIds = new List<string>();
        fixture.Bm25.UploadGridFsBlobAsync(Arg.Any<string>(),
                                           Arg.Any<Stream>(),
                                           Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                uploadedIds.Add(call.ArgAt<string>(0));
                                return Task.CompletedTask;
                            });
        fixture.Bm25.UpsertShardAsync(Arg.Any<Bm25Shard>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException(new IOException("forced shard failure")));
        fixture.Deletion.DeleteVersionUnderModeLeaseAsync(profile: null,
                                                           LibraryId,
                                                           Version,
                                                           fixture.ModeLease,
                                                           Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<LibraryDeletionResult>(
                            new IOException("forced version deletion failure")));

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.NotEmpty(uploadedIds);
        Assert.Contains(exception.Flatten().InnerExceptions,
                        failure => failure.Message.Contains("remained after cleanup", StringComparison.Ordinal));
        await fixture.Bm25.DidNotReceiveWithAnyArgs()
                     .DeleteGridFsBlobAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NewDirectoryGateFailureAbandonsReservationWithoutDeletingOwnership()
    {
        string bundle = CreateValidBundle();
        ImportFixture fixture = Fixture(existingDefinition: null);
        fixture.Jobs.ListActiveAsync(LibraryId,
                                     Version,
                                     Arg.Any<JobType?>(),
                                     Arg.Any<CancellationToken>())
               .Returns([new JobRecord
                             {
                                 Id = "running-job",
                                 JobType = JobType.DirectoryScan,
                                 Status = JobStatus.Running,
                                 LibraryId = LibraryId,
                                 Version = Version
                             }]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Importer.ImportAsync(
                                                                  new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken));

        await fixture.ModeLease.Received(requiredNumberOfCalls: 1)
                     .TryAbandonReservationAsync(Arg.Any<CancellationToken>());
        await fixture.ModeLease.DidNotReceiveWithAnyArgs()
                     .TryCommitAsync(TestContext.Current.CancellationToken);
        await fixture.ModeLease.DidNotReceiveWithAnyArgs()
                     .TryDeleteOwnershipAsync(TestContext.Current.CancellationToken);
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteLibraryUnderModeLeaseAsync(default,
                                                       default!,
                                                       default!,
                                                       TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReservedDirectoryModeForExistingDefinitionIsAbandonedOnPreWriteConflict()
    {
        string bundle = CreateValidBundle();
        ImportFixture fixture = Fixture(ExistingDefinition());
        fixture.ModeLease.OwnershipStateAtAcquisition.Returns(
            LibraryIngestionOwnershipState.Reserved);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Importer.ImportAsync(
                                                                  new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken));

        await fixture.ModeLease.Received(requiredNumberOfCalls: 1)
                     .TryAbandonReservationAsync(Arg.Any<CancellationToken>());
        await fixture.ModeLease.DidNotReceiveWithAnyArgs()
                     .TryCommitAsync(TestContext.Current.CancellationToken);
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteLibraryUnderModeLeaseAsync(default,
                                                       default!,
                                                       default!,
                                                       TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AcquisitionFailureAndAllDisposalFailuresAreAggregated()
    {
        string bundle = CreateValidBundle();
        ImportFixture fixture = Fixture(ExistingDefinition());
        fixture.ModeLease.TryRenewAsync(Arg.Any<CancellationToken>())
               .Returns(_ => ValueTask.FromException<bool>(
                            new InvalidOperationException("forced acquisition failure")));
        fixture.PublicationLease!.DisposeAsync()
               .Returns(_ => ValueTask.FromException(
                            new InvalidOperationException("forced publication dispose failure")));
        fixture.ModeLease.DisposeAsync()
               .Returns(_ => ValueTask.FromException(
                            new InvalidOperationException("forced mode dispose failure")));

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        IReadOnlyList<Exception> failures = exception.Flatten().InnerExceptions;
        Assert.Contains(failures, failure => failure.Message == "forced acquisition failure");
        Assert.Contains(failures, failure => failure.Message == "forced publication dispose failure");
        Assert.Contains(failures, failure => failure.Message == "forced mode dispose failure");
        await fixture.PublicationLease!.Received(requiredNumberOfCalls: 1).DisposeAsync();
        await fixture.ModeLease.Received(requiredNumberOfCalls: 1).DisposeAsync();
    }

    [Fact]
    public async Task ImportFailureAndPublicationDisposeFailureAreAggregatedWhileModeLeaseStillDisposes()
    {
        string bundle = CreateValidBundle();
        ImportFixture fixture = Fixture(ExistingDefinition());
        bool modeDisposed = false;
        fixture.PublicationLease!.DisposeAsync()
               .Returns(_ => ValueTask.FromException(
                            new InvalidOperationException("forced publication dispose failure")));
        fixture.ModeLease.DisposeAsync().Returns(_ =>
                                                   {
                                                       modeDisposed = true;
                                                       return ValueTask.CompletedTask;
                                                   });

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        IReadOnlyList<Exception> failures = exception.Flatten().InnerExceptions;
        Assert.Contains(failures,
                        failure => failure.Message.Contains("Versions already present", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Message == "forced publication dispose failure");
        Assert.True(modeDisposed);
        await fixture.PublicationLease!.Received(requiredNumberOfCalls: 1).DisposeAsync();
        await fixture.ModeLease.Received(requiredNumberOfCalls: 1).DisposeAsync();
    }

    [Fact]
    public async Task AbandonFailureStillDisposesModeLeaseAndPreservesImportFailure()
    {
        string bundle = CreateValidBundle();
        ImportFixture fixture = Fixture(existingDefinition: null);
        bool modeDisposed = false;
        fixture.Jobs.ListActiveAsync(LibraryId,
                                     Version,
                                     Arg.Any<JobType?>(),
                                     Arg.Any<CancellationToken>())
               .Returns([new JobRecord
                             {
                                 Id = "running-job",
                                 JobType = JobType.DirectoryScan,
                                 Status = JobStatus.Running,
                                 LibraryId = LibraryId,
                                 Version = Version
                             }]);
        fixture.ModeLease.TryAbandonReservationAsync(Arg.Any<CancellationToken>())
               .Returns(_ => ValueTask.FromException<bool>(
                             new InvalidOperationException("forced abandon failure")));
        fixture.ModeLease.DisposeAsync().Returns(_ =>
                                                   {
                                                       modeDisposed = true;
                                                       return ValueTask.CompletedTask;
                                                   });

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        IReadOnlyList<Exception> failures = exception.Flatten().InnerExceptions;
        Assert.Contains(failures,
                        failure => failure.Message.Contains("running for", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Message == "forced abandon failure");
        Assert.True(modeDisposed);
        await fixture.ModeLease.Received(requiredNumberOfCalls: 1)
                     .TryAbandonReservationAsync(Arg.Any<CancellationToken>());
        await fixture.ModeLease.Received(requiredNumberOfCalls: 1).DisposeAsync();
    }

    [Fact]
    public async Task DirectoryOrphanDataRefusalAbandonsItsReservedMode()
    {
        string bundle = CreateValidBundle();
        ImportFixture fixture = Fixture(existingDefinition: null);
        fixture.Modes.HasAnyLibraryDataAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Importer.ImportAsync(
                                                                  new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken));

        await fixture.ModeLease.Received(requiredNumberOfCalls: 1)
                     .TryAbandonReservationAsync(Arg.Any<CancellationToken>());
        await fixture.ModeLease.DidNotReceiveWithAnyArgs()
                     .TryCommitAsync(TestContext.Current.CancellationToken);
        await fixture.ModeLease.DidNotReceiveWithAnyArgs()
                     .TryDeleteOwnershipAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WebPackageRejectsDirectoryOwnedLibraryId()
    {
        string bundle = CreateValidBundle(isDirectory: false);
        ImportFixture fixture = Fixture(ExistingDefinition());
        fixture.ModeManager.TryAcquireAsync(profile: null,
                                            LibraryId,
                                            LibraryIngestionMode.Web,
                                            Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<ILibraryIngestionModeLease?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Importer.ImportAsync(
                                                                  new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken));

        await fixture.ModeManager.Received(requiredNumberOfCalls: 1)
                     .TryAcquireAsync(profile: null,
                                      LibraryId,
                                      LibraryIngestionMode.Web,
                                      Arg.Any<CancellationToken>());
        await fixture.ModeLease.DidNotReceiveWithAnyArgs()
                     .TryCommitAsync(TestContext.Current.CancellationToken);
        await fixture.Libraries.DidNotReceiveWithAnyArgs()
                     .UpsertVersionAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WebPackageReconcilesOrphanDirectoryDefinitionBeforeRefusal()
    {
        string bundle = CreateValidBundle(isDirectory: false);
        ImportFixture fixture = Fixture(existingDefinition: null);
        fixture.ModeLease.Mode.Returns(LibraryIngestionMode.Web);
        fixture.ModeLease.OwnershipStateAtAcquisition.Returns(LibraryIngestionOwnershipState.Reserved);
        fixture.ModeLease.TryReconcileReservedModeAsync(LibraryIngestionMode.Directory,
                                                        Arg.Any<CancellationToken>())
               .Returns(true);
        fixture.Modes.GetLibraryDataEvidenceAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(new LibraryIngestionDataEvidence(false, true, false, false, false));
        fixture.ModeManager.TryAcquireAsync(Arg.Any<string?>(),
                                            LibraryId,
                                            LibraryIngestionMode.Web,
                                            Arg.Any<CancellationToken>())
               .Returns(fixture.ModeLease);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Importer.ImportAsync(
                                                                  new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken));

        await fixture.ModeLease.Received(requiredNumberOfCalls: 1)
                     .TryReconcileReservedModeAsync(LibraryIngestionMode.Directory,
                                                    Arg.Any<CancellationToken>());
        await fixture.ModeLease.DidNotReceiveWithAnyArgs()
                     .TryAbandonReservationAsync(TestContext.Current.CancellationToken);
        await fixture.ModeLease.DidNotReceiveWithAnyArgs()
                     .TryCommitAsync(TestContext.Current.CancellationToken);
        await fixture.Libraries.DidNotReceiveWithAnyArgs()
                     .UpsertVersionAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WebPackageAcceptsLegacyWebDocumentProvenance()
    {
        string bundle = CreateValidBundle(isDirectory: false);
        ImportFixture fixture = Fixture(existingDefinition: null);
        fixture.ModeLease.Mode.Returns(LibraryIngestionMode.Web);
        fixture.ModeLease.OwnershipStateAtAcquisition.Returns(LibraryIngestionOwnershipState.Reserved);
        fixture.Modes.GetLibraryDataEvidenceAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(new LibraryIngestionDataEvidence(true, false, true, true, false));
        fixture.ModeManager.TryAcquireAsync(Arg.Any<string?>(),
                                            LibraryId,
                                            LibraryIngestionMode.Web,
                                            Arg.Any<CancellationToken>())
               .Returns(fixture.ModeLease);

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.Equal([Version], result.VersionsImported);
        await fixture.ModeLease.Received(requiredNumberOfCalls: 1)
                     .TryCommitAsync(Arg.Any<CancellationToken>());
        await fixture.ModeLease.DidNotReceiveWithAnyArgs()
                     .TryReconcileReservedModeAsync(Arg.Any<LibraryIngestionMode>(),
                                                    Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WebOverwriteHoldsOneModeLeaseThroughPurgeWriteAndLibraryPublication()
    {
        string bundle = CreateValidBundle(isDirectory: false);
        ImportFixture fixture = WebFixture();
        bool disposed = false;
        fixture.ModeLease.DisposeAsync().Returns(_ =>
                                                   {
                                                       disposed = true;
                                                       return ValueTask.CompletedTask;
                                                   });
        fixture.Deletion.DeleteVersionUnderModeLeaseAsync(profile: null,
                                                           LibraryId,
                                                           Version,
                                                           fixture.ModeLease,
                                                           Arg.Any<CancellationToken>())
               .Returns(_ =>
                            {
                                Assert.False(disposed);
                                return EmptyDeletionResult;
                            });
        fixture.Libraries.UpsertVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                              Arg.Any<CancellationToken>())
               .Returns(_ =>
                            {
                                Assert.False(disposed);
                                return Task.CompletedTask;
                            });
        fixture.Libraries.UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>())
               .Returns(_ =>
                            {
                                Assert.False(disposed);
                                return Task.CompletedTask;
                            });

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest
                                                                      {
                                                                          BundlePath = bundle,
                                                                          Overwrite = true
                                                                      },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.True(disposed);
        Assert.Equal([Version], result.OverwrittenVersions);
        await fixture.ModeLease.Received(requiredNumberOfCalls: 1)
                     .TryCommitAsync(Arg.Any<CancellationToken>());
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteVersionUnderModeLeaseAsync(profile: null,
                                                       LibraryId,
                                                       Version,
                                                       fixture.ModeLease,
                                                       Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WebWriteFailureRollsBackUnderTheSameHeldModeLease()
    {
        string bundle = CreateValidBundle(isDirectory: false);
        ImportFixture fixture = WebFixture();
        bool disposed = false;
        LibraryVersionRecord? durableVersion = null;
        fixture.ModeLease.DisposeAsync().Returns(_ =>
                                                   {
                                                       disposed = true;
                                                       return ValueTask.CompletedTask;
                                                   });
        fixture.Deletion.DeleteVersionUnderModeLeaseAsync(profile: null,
                                                           LibraryId,
                                                           Version,
                                                           fixture.ModeLease,
                                                           Arg.Any<CancellationToken>())
               .Returns(_ =>
                            {
                                Assert.False(disposed);
                                durableVersion = null;
                                return EmptyDeletionResult;
                            });
        fixture.Libraries.TryClaimImportVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                                     Arg.Any<string>(),
                                                     Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                durableVersion = call.ArgAt<LibraryVersionRecord>(0);
                                return true;
                            });
        fixture.Libraries.GetVersionAsync(LibraryId, Version, Arg.Any<CancellationToken>())
               .Returns(_ => durableVersion);
        fixture.Libraries.TryPublishImportVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                                       Arg.Any<string>(),
                                                       Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<bool>(
                            new InvalidOperationException("forced web write failure")));

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest
                                                                      {
                                                                          BundlePath = bundle,
                                                                          Overwrite = true
                                                                      },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.True(disposed);
        Assert.Single(result.PartialFailures);
        await fixture.Deletion.Received(requiredNumberOfCalls: 2)
                     .DeleteVersionUnderModeLeaseAsync(profile: null,
                                                       LibraryId,
                                                       Version,
                                                       fixture.ModeLease,
                                                       Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistingDirectoryLeaseLossPreventsPurgeAndWrites()
    {
        string bundle = CreateValidBundle();
        DirectoryLibraryDefinition existing = ExistingDefinition();
        ImportFixture fixture = Fixture(existing);
        fixture.PublicationLease!.TryRenewAsync(Arg.Any<CancellationToken>()).Returns(false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Importer.ImportAsync(
                                                                  new ImportRequest
                                                                      {
                                                                          BundlePath = bundle,
                                                                          Overwrite = true
                                                                      },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken));

        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteScanCandidateUnderLeaseAsync(default,
                                                         default!,
                                                         default!,
                                                         default!,
                                                         default!,
                                                         TestContext.Current.CancellationToken);
        await fixture.Libraries.DidNotReceiveWithAnyArgs()
                     .UpsertVersionAsync(default!, TestContext.Current.CancellationToken);
        await fixture.Sources.DidNotReceiveWithAnyArgs()
                     .TryApplyDirectoryPackagePublicationAsync(default!,
                                                               default,
                                                               default!,
                                                               default,
                                                               default!,
                                                               TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExistingOverwriteUsesOneLeaseForPurgeAndFinalPackagePublication()
    {
        string bundle = CreateValidBundle();
        DirectoryLibraryDefinition existing = ExistingDefinition();
        ImportFixture fixture = Fixture(existing);

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest
                                                                      {
                                                                          BundlePath = bundle,
                                                                          Overwrite = true
                                                                      },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.Equal([Version], result.OverwrittenVersions);
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                         LibraryId,
                                                         Version,
                                                         fixture.PublicationLease!,
                                                         fixture.ModeLease,
                                                         Arg.Any<CancellationToken>());
        await fixture.Sources.Received(requiredNumberOfCalls: 1)
                     .TryApplyDirectoryPackagePublicationAsync(
                          fixture.PublicationLease!,
                          existing.LastPublishedVersion,
                          Arg.Is<DirectoryLibraryDefinition>(definition =>
                              definition != null &&
                              definition.Id == LibraryId &&
                             definition.BindingStatus == DirectoryLibraryBindingStatus.Unbound),
                         VersionRecord().ScrapedAt,
                         Version,
                         Arg.Any<CancellationToken>());
        await fixture.Sources.DidNotReceiveWithAnyArgs()
                     .UpsertDirectoryDefinitionAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CurrentPurgeSuccessThenLaterPurgeFailureRepairsBothPublicationSummaries()
    {
        string bundle = CreateValidBundle([Version, LaterVersion]);
        LibraryRecord previousLibrary = new()
                                            {
                                                Id = LibraryId,
                                                Name = "Local library name",
                                                Hint = "Local library hint",
                                                CurrentVersion = Version,
                                                AllVersions = [PreviousVersion, Version, LaterVersion]
                                            };
        LibraryVersionRecord previousRow = PackagingFixtures.MakeVersion(
            LibraryId,
            PreviousVersion,
            pageCount: 0,
            chunkCount: 0,
            dim: PackagingFixtures.DefaultDim) with
                                                        {
                                                            ScrapedAt = new DateTime(2025,
                                                                                     12,
                                                                                     1,
                                                                                     0,
                                                                                     0,
                                                                                     0,
                                                                                     DateTimeKind.Utc)
                                                        };
        LibraryVersionRecord currentRow = VersionRecord();
        LibraryVersionRecord laterRow = PackagingFixtures.MakeVersion(
            LibraryId,
            LaterVersion,
            pageCount: 0,
            chunkCount: 0,
            dim: PackagingFixtures.DefaultDim);
        string[] survivingVersions = [PreviousVersion];
        ImportFixture fixture = Fixture(ExistingDefinition(), previousLibrary);
        fixture.Libraries.GetVersionsAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns([previousRow, currentRow, laterRow]);
        var deletionCalls = 0;
        fixture.Deletion.DeleteScanCandidateUnderLeaseAsync(
                                                             Arg.Is<string?>(profile => profile == null),
                                                             Arg.Is<string>(libraryId =>
                                                                 string.Equals(libraryId,
                                                                               LibraryId,
                                                                               StringComparison.Ordinal)),
                                                             Arg.Is<string>(version =>
                                                                 string.Equals(version,
                                                                               Version,
                                                                               StringComparison.Ordinal) ||
                                                                 string.Equals(version,
                                                                               LaterVersion,
                                                                               StringComparison.Ordinal)),
                                                             fixture.PublicationLease!,
                                                             fixture.ModeLease,
                                                             Arg.Any<CancellationToken>())
               .Returns(_ => ++deletionCalls == 1
                                 ? EmptyDeletionResult
                                 : throw new InvalidOperationException("forced later purge failure"));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest
                                             {
                                                 BundlePath = bundle,
                                                 Overwrite = true
                                             },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Equal("forced later purge failure", exception.Message);
        Assert.Equal(2, deletionCalls);
        await fixture.Libraries.Received(requiredNumberOfCalls: 1)
                     .TryReplaceLibrarySummaryAsync(
                           Arg.Is<LibraryRecord>(library => LibraryMatches(library, previousLibrary)),
                           Arg.Is<LibraryRecord>(library =>
                               library != null &&
                               string.Equals(library.CurrentVersion, PreviousVersion, StringComparison.Ordinal) &&
                               library.AllVersions.SequenceEqual(survivingVersions, StringComparer.Ordinal)),
                           Arg.Any<CancellationToken>());
        await fixture.Sources.Received(requiredNumberOfCalls: 1)
                     .TryUpdateDirectoryPublicationAsync(fixture.PublicationLease!,
                                                         Version,
                                                         previousRow.ScrapedAt,
                                                         PreviousVersion,
                                                         Arg.Any<CancellationToken>());
        await fixture.Libraries.DidNotReceiveWithAnyArgs()
                     .TryClaimImportVersionAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PurgeWriteThenThrowAndLibraryReplaceWriteThenThrowAreRecovered()
    {
        string bundle = CreateValidBundle();
        LibraryRecord previousLibrary = new()
                                            {
                                                Id = LibraryId,
                                                Name = "Local library name",
                                                Hint = "Local library hint",
                                                CurrentVersion = Version,
                                                AllVersions = [PreviousVersion, Version]
                                            };
        LibraryRecord desiredLibrary = new()
                                           {
                                               Id = LibraryId,
                                               Name = previousLibrary.Name,
                                               Hint = previousLibrary.Hint,
                                               CurrentVersion = PreviousVersion,
                                               AllVersions = [PreviousVersion]
                                           };
        LibraryVersionRecord previousRow = PackagingFixtures.MakeVersion(
            LibraryId,
            PreviousVersion,
            pageCount: 0,
            chunkCount: 0,
            dim: PackagingFixtures.DefaultDim);
        ImportFixture fixture = Fixture(ExistingDefinition(), previousLibrary);
        var libraryReads = 0;
        fixture.Libraries.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(_ => ++libraryReads == 3 ? desiredLibrary : previousLibrary);
        fixture.Libraries.GetVersionsAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns([previousRow, VersionRecord()]);
        fixture.Deletion.DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                             LibraryId,
                                                             Version,
                                                             fixture.PublicationLease!,
                                                             fixture.ModeLease,
                                                             Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<LibraryDeletionResult>(
                            new InvalidOperationException("forced purge write-then-throw")));
        fixture.Libraries.TryReplaceLibrarySummaryAsync(Arg.Any<LibraryRecord>(),
                                                         Arg.Any<LibraryRecord>(),
                                                         Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<bool>(
                            new InvalidOperationException("forced summary write-then-throw")));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest
                                             {
                                                 BundlePath = bundle,
                                                 Overwrite = true
                                             },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Equal("forced purge write-then-throw", exception.Message);
        Assert.Equal(3, libraryReads);
        await fixture.Libraries.Received(requiredNumberOfCalls: 1)
                     .TryReplaceLibrarySummaryAsync(
                          Arg.Is<LibraryRecord>(library => LibraryMatches(library, previousLibrary)),
                          Arg.Is<LibraryRecord>(library => LibraryMatches(library, desiredLibrary)),
                          Arg.Any<CancellationToken>());
        await fixture.Sources.Received(requiredNumberOfCalls: 1)
                     .TryUpdateDirectoryPublicationAsync(fixture.PublicationLease!,
                                                         Version,
                                                         previousRow.ScrapedAt,
                                                         PreviousVersion,
                                                         Arg.Any<CancellationToken>());
        await fixture.Libraries.DidNotReceiveWithAnyArgs()
                     .TryClaimImportVersionAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LastVersionPurgeWriteThenThrowDeletesOnlyStaleSummaries()
    {
        string bundle = CreateValidBundle();
        LibraryRecord previousLibrary = new()
                                            {
                                                Id = LibraryId,
                                                Name = "Local library name",
                                                Hint = "Local library hint",
                                                CurrentVersion = Version,
                                                AllVersions = [Version]
                                            };
        ImportFixture fixture = Fixture(ExistingDefinition(), previousLibrary);
        var libraryReads = 0;
        fixture.Libraries.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(_ =>
                            {
                                libraryReads++;
                                LibraryRecord? result = libraryReads >= 3 ? null : previousLibrary;
                                return result;
                            });
        fixture.Libraries.GetVersionsAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns([VersionRecord()]);
        fixture.Deletion.DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                             LibraryId,
                                                             Version,
                                                             fixture.PublicationLease!,
                                                             fixture.ModeLease,
                                                             Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<LibraryDeletionResult>(
                            new InvalidOperationException("forced purge write-then-throw")));
        fixture.Libraries.TryDeleteLibrarySummaryAsync(Arg.Any<LibraryRecord>(),
                                                        Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<bool>(
                            new InvalidOperationException("forced summary delete write-then-throw")));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest
                                             {
                                                 BundlePath = bundle,
                                                 Overwrite = true
                                             },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Equal("forced purge write-then-throw", exception.Message);
        Assert.Equal(3, libraryReads);
        await fixture.Libraries.Received(requiredNumberOfCalls: 1)
                     .TryDeleteLibrarySummaryAsync(
                          Arg.Is<LibraryRecord>(library => LibraryMatches(library, previousLibrary)),
                          Arg.Any<CancellationToken>());
        await fixture.Sources.Received(requiredNumberOfCalls: 1)
                     .TryUpdateDirectoryPublicationAsync(fixture.PublicationLease!,
                                                         Version,
                                                         publishedAtUtc: null,
                                                         publishedVersion: null,
                                                         Arg.Any<CancellationToken>());
        await fixture.Sources.DidNotReceiveWithAnyArgs()
                     .TryDeleteLeasedDirectoryDefinitionAsync(default!, TestContext.Current.CancellationToken);
        await fixture.Libraries.DidNotReceiveWithAnyArgs()
                     .TryClaimImportVersionAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExistingDirectoryOverwriteWriteFailureClearsPurgedPublicationPointer()
    {
        string bundle = CreateValidBundle();
        LibraryRecord previousLibrary = PackagingFixtures.MakeLibrary(LibraryId, Version);
        ImportFixture fixture = Fixture(ExistingDefinition(), previousLibrary);
        var libraryReads = 0;
        fixture.Libraries.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(_ =>
                            {
                                libraryReads++;
                                LibraryRecord? result = libraryReads == 1 ? previousLibrary : null;
                                return result;
                            });
        fixture.Libraries.TryPublishImportVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                                       Arg.Any<string>(),
                                                       Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<bool>(
                            new InvalidOperationException("forced replacement write failure")));

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest
                                                                      {
                                                                          BundlePath = bundle,
                                                                          Overwrite = true
                                                                      },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        IDirectoryPublicationLease publicationLease = Assert.IsAssignableFrom<IDirectoryPublicationLease>(
            fixture.PublicationLease);
        Assert.Single(result.PartialFailures);
        await fixture.Deletion.Received(requiredNumberOfCalls: 2)
                     .DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                         LibraryId,
                                                         Version,
                                                         publicationLease,
                                                         fixture.ModeLease,
                                                         Arg.Any<CancellationToken>());
        await fixture.Sources.Received(requiredNumberOfCalls: 1)
                     .TryUpdateDirectoryPublicationAsync(publicationLease,
                                                         Version,
                                                         publishedAtUtc: null,
                                                         publishedVersion: null,
                                                         Arg.Any<CancellationToken>());
        await fixture.Libraries.DidNotReceive()
                     .UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>());
        await fixture.Sources.DidNotReceiveWithAnyArgs()
                     .TryApplyDirectoryPackagePublicationAsync(default!,
                                                               default,
                                                               default!,
                                                               default,
                                                               default!,
                                                               TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FailedOverwriteReconcilesToSurvivingVersionWhenRollbackDeletionFails()
    {
        string bundle = CreateValidBundle();
        LibraryRecord previousLibrary = new()
                                            {
                                                Id = LibraryId,
                                                Name = "Local library name",
                                                Hint = "Local library hint",
                                                CurrentVersion = Version,
                                                AllVersions = [PreviousVersion, Version]
                                            };
        ImportFixture fixture = Fixture(ExistingDefinition(), previousLibrary);
        var deletionCalls = 0;
        fixture.Deletion.DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                             LibraryId,
                                                             Version,
                                                             fixture.PublicationLease!,
                                                             fixture.ModeLease,
                                                             Arg.Any<CancellationToken>())
               .Returns(_ => ++deletionCalls == 1
                                 ? EmptyDeletionResult
                                 : throw new InvalidOperationException("forced rollback deletion failure"));
        fixture.Libraries.TryPublishImportVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                                       Arg.Any<string>(),
                                                       Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<bool>(
                            new InvalidOperationException("forced replacement write failure")));

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest
                                             {
                                                 BundlePath = bundle,
                                                 Overwrite = true
                                             },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Contains(exception.Flatten().InnerExceptions,
                        failure => failure.Message == "forced rollback deletion failure");
        Assert.Equal(2, deletionCalls);
        await fixture.Sources.Received(requiredNumberOfCalls: 1)
                     .TryUpdateDirectoryPublicationAsync(fixture.PublicationLease!,
                                                         Version,
                                                         Arg.Any<DateTime?>(),
                                                         PreviousVersion,
                                                         Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistingDirectoryLibraryUpsertFailureRollsBackBeforePackagePublication()
    {
        string bundle = CreateValidBundle();
        LibraryRecord previousLibrary = PreviousLibrary();
        ImportFixture fixture = Fixture(ExistingDefinition(PreviousVersion), previousLibrary);
        var upsertAttempts = 0;
        fixture.Libraries.UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>())
               .Returns(_ =>
                            {
                                upsertAttempts++;
                                Task result = upsertAttempts == 1
                                                  ? Task.FromException(
                                                      new InvalidOperationException("forced library upsert failure"))
                                                  : Task.CompletedTask;
                                return result;
                            });

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Equal("forced library upsert failure", exception.Message);
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                         LibraryId,
                                                         Version,
                                                         fixture.PublicationLease!,
                                                         fixture.ModeLease,
                                                         Arg.Any<CancellationToken>());
        Assert.Equal(1, upsertAttempts);
        await fixture.Libraries.Received(requiredNumberOfCalls: 1)
                     .UpsertLibraryAsync(Arg.Is<LibraryRecord>(library =>
                                             library != null &&
                                             string.Equals(library.CurrentVersion,
                                                           Version,
                                                           StringComparison.Ordinal)),
                                         Arg.Any<CancellationToken>());
        await fixture.Sources.DidNotReceiveWithAnyArgs()
                     .TryApplyDirectoryPackagePublicationAsync(default!,
                                                               default,
                                                               default!,
                                                               default,
                                                               default!,
                                                               TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExistingDirectoryPublicationFailureRemovesQueuedReembedAndRestoresLibrary()
    {
        string bundle = CreateValidBundle(encoderMatches: false);
        LibraryRecord previousLibrary = PreviousLibrary();
        ImportFixture fixture = Fixture(ExistingDefinition(PreviousVersion), previousLibrary);
        IDirectoryPublicationLease publicationLease =
            Assert.IsAssignableFrom<IDirectoryPublicationLease>(fixture.PublicationLease);
        fixture.Sources.TryApplyDirectoryPackagePublicationAsync(Arg.Any<IDirectoryPublicationLease>(),
                                                                  Arg.Any<string?>(),
                                                                  Arg.Any<DirectoryLibraryDefinition>(),
                                                                  Arg.Any<DateTime>(),
                                                                  Arg.Any<string>(),
                                                                  Arg.Any<CancellationToken>())
               .Returns(false);
        JobRecord? queuedJob = null;
        fixture.Jobs.UpsertAsync(Arg.Any<JobRecord>(), Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                queuedJob = call.ArgAt<JobRecord>(0);
                                return Task.CompletedTask;
                            });

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Importer.ImportAsync(
                                                                new ImportRequest { BundlePath = bundle },
                                                                progress: null,
                                                                TestContext.Current.CancellationToken));

        JobRecord persistedJob = Assert.IsType<JobRecord>(queuedJob);
        await fixture.Jobs.Received(requiredNumberOfCalls: 1)
                     .DeleteAsync(persistedJob.Id, Arg.Any<CancellationToken>());
        fixture.ReembedDispatcher.DidNotReceive().TryDispatchPersisted(Arg.Any<JobRecord>());
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                         LibraryId,
                                                         Version,
                                                         publicationLease,
                                                         fixture.ModeLease,
                                                         Arg.Any<CancellationToken>());
        await fixture.Libraries.Received(requiredNumberOfCalls: 1)
                     .UpsertLibraryAsync(Arg.Any<LibraryRecord>(),
                                         Arg.Any<CancellationToken>());
        await fixture.Libraries.Received(requiredNumberOfCalls: 1)
                     .UpsertLibraryAsync(Arg.Is<LibraryRecord>(library =>
                                             library != null &&
                                             string.Equals(library.CurrentVersion,
                                                           Version,
                                                           StringComparison.Ordinal)),
                                         Arg.Any<CancellationToken>());
        await fixture.Sources.Received(requiredNumberOfCalls: 1)
                     .TryApplyDirectoryPackagePublicationAsync(publicationLease,
                                                               Arg.Any<string?>(),
                                                               Arg.Any<DirectoryLibraryDefinition>(),
                                                               Arg.Any<DateTime>(),
                                                               Version,
                                                               Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeaseLossDuringVersionCleanupPreventsJobDeletionAndLibraryRestore()
    {
        string bundle = CreateValidBundle(encoderMatches: false);
        LibraryRecord previousLibrary = PreviousLibrary();
        ImportFixture fixture = Fixture(ExistingDefinition(PreviousVersion), previousLibrary);
        using var ownershipLost = new CancellationTokenSource();
        fixture.ModeLease.OwnershipLostToken.Returns(ownershipLost.Token);
        fixture.Sources.TryApplyDirectoryPackagePublicationAsync(Arg.Any<IDirectoryPublicationLease>(),
                                                                  Arg.Any<string?>(),
                                                                  Arg.Any<DirectoryLibraryDefinition>(),
                                                                  Arg.Any<DateTime>(),
                                                                  Arg.Any<string>(),
                                                                  Arg.Any<CancellationToken>())
               .Returns(false);
        fixture.Deletion.DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                             LibraryId,
                                                             Version,
                                                             fixture.PublicationLease!,
                                                             fixture.ModeLease,
                                                             Arg.Any<CancellationToken>())
               .Returns(_ =>
                            {
                                ownershipLost.Cancel();
                                return Task.FromException<LibraryDeletionResult>(
                                    new IOException("forced candidate cleanup failure"));
                            });
        JobRecord? queuedJob = null;
        fixture.Jobs.UpsertAsync(Arg.Any<JobRecord>(), Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                queuedJob = call.ArgAt<JobRecord>(0);
                                return Task.CompletedTask;
                            });

        await Assert.ThrowsAsync<AggregateException>(() => fixture.Importer.ImportAsync(
                                                           new ImportRequest { BundlePath = bundle },
                                                           progress: null,
                                                           TestContext.Current.CancellationToken));

        JobRecord persistedJob = Assert.IsType<JobRecord>(queuedJob);
        await fixture.Jobs.DidNotReceive()
                     .DeleteAsync(persistedJob.Id, Arg.Any<CancellationToken>());
        fixture.ReembedDispatcher.DidNotReceive().TryDispatchPersisted(Arg.Any<JobRecord>());
        await fixture.Libraries.DidNotReceive()
                     .UpsertLibraryAsync(Arg.Is<LibraryRecord>(library =>
                                             LibraryMatches(library, previousLibrary)),
                                         Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AmbiguousPublicationOwnedByDifferentLeaseIsNotConfirmed()
    {
        string bundle = CreateValidBundle(encoderMatches: false);
        DirectoryLibraryDefinition existing = ExistingDefinition(PreviousVersion);
        LibraryRecord previousLibrary = PreviousLibrary();
        ImportFixture fixture = Fixture(existing, previousLibrary);
        DirectoryLibraryDefinition? durableDefinition = null;
        var definitionReads = 0;
        fixture.Sources.GetDirectoryDefinitionAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(_ => ++definitionReads == 1 ? existing : durableDefinition);
        fixture.Sources.TryApplyDirectoryPackagePublicationAsync(Arg.Any<IDirectoryPublicationLease>(),
                                                                  Arg.Any<string?>(),
                                                                  Arg.Any<DirectoryLibraryDefinition>(),
                                                                  Arg.Any<DateTime>(),
                                                                  Arg.Any<string>(),
                                                                  Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                DirectoryLibraryDefinition package = call.ArgAt<DirectoryLibraryDefinition>(2);
                                durableDefinition = package with
                                                        {
                                                            RegistrationRevision = existing.RegistrationRevision,
                                                            RegistrationIncarnationId =
                                                                existing.RegistrationIncarnationId,
                                                            PublicationLeaseScanRunId = "different-lease",
                                                            PublicationLeaseRegistrationRevision =
                                                                existing.RegistrationRevision,
                                                            LastPublishedAtUtc = call.ArgAt<DateTime>(3),
                                                            LastPublishedVersion = call.ArgAt<string>(4)
                                                        };
                                return Task.FromException<bool>(
                                    new InvalidOperationException("ambiguous package publication"));
                            });
        JobRecord? queuedJob = null;
        fixture.Jobs.UpsertAsync(Arg.Any<JobRecord>(), Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                queuedJob = call.ArgAt<JobRecord>(0);
                                return Task.CompletedTask;
                            });

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Contains(exception.Flatten().InnerExceptions,
                        failure => failure.Message.Contains("could not be attributed", StringComparison.Ordinal));
        Assert.Equal(2, definitionReads);
        JobRecord persistedJob = Assert.IsType<JobRecord>(queuedJob);
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteScanCandidateUnderLeaseAsync(default,
                                                         default!,
                                                         default!,
                                                         default!,
                                                         default!,
                                                         TestContext.Current.CancellationToken);
        await fixture.Jobs.DidNotReceive()
                     .DeleteAsync(persistedJob.Id, Arg.Any<CancellationToken>());
        fixture.ReembedDispatcher.DidNotReceive().TryDispatchPersisted(Arg.Any<JobRecord>());
        await fixture.Libraries.Received(requiredNumberOfCalls: 1)
                     .UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>());
        await fixture.Libraries.DidNotReceive()
                     .UpsertLibraryAsync(Arg.Is<LibraryRecord>(library =>
                                             LibraryMatches(library, previousLibrary)),
                                         Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnconfirmedAmbiguousPublicationDoesNotDestructivelyCleanImportedState()
    {
        string bundle = CreateValidBundle(encoderMatches: false);
        DirectoryLibraryDefinition existing = ExistingDefinition(PreviousVersion);
        LibraryRecord previousLibrary = PreviousLibrary();
        ImportFixture fixture = Fixture(existing, previousLibrary);
        var definitionReads = 0;
        fixture.Sources.GetDirectoryDefinitionAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(_ =>
                            {
                                definitionReads++;
                                if (definitionReads == 1)
                                    return existing;
                                throw new IOException("forced publication confirmation failure");
                            });
        fixture.Sources.TryApplyDirectoryPackagePublicationAsync(Arg.Any<IDirectoryPublicationLease>(),
                                                                  Arg.Any<string?>(),
                                                                  Arg.Any<DirectoryLibraryDefinition>(),
                                                                  Arg.Any<DateTime>(),
                                                                  Arg.Any<string>(),
                                                                  Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<bool>(
                            new InvalidOperationException("ambiguous package publication")));
        JobRecord? queuedJob = null;
        fixture.Jobs.UpsertAsync(Arg.Any<JobRecord>(), Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                queuedJob = call.ArgAt<JobRecord>(0);
                                return Task.CompletedTask;
                            });

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Contains("durable outcome could not be confirmed", exception.Message,
                        StringComparison.Ordinal);
        Assert.Equal(2, definitionReads);
        JobRecord persistedJob = Assert.IsType<JobRecord>(queuedJob);
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteScanCandidateUnderLeaseAsync(default,
                                                         default!,
                                                         default!,
                                                         default!,
                                                         default!,
                                                         TestContext.Current.CancellationToken);
        await fixture.Jobs.DidNotReceive()
                     .DeleteAsync(persistedJob.Id, Arg.Any<CancellationToken>());
        await fixture.Libraries.Received(requiredNumberOfCalls: 1)
                     .UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistingDirectoryOverwriteFailurePreservesReplacementAndRequiredReembedJob()
    {
        string bundle = CreateValidBundle(encoderMatches: false);
        LibraryRecord previousLibrary = PackagingFixtures.MakeLibrary(LibraryId, Version);
        ImportFixture fixture = Fixture(ExistingDefinition(), previousLibrary);
        IDirectoryPublicationLease publicationLease =
            Assert.IsAssignableFrom<IDirectoryPublicationLease>(fixture.PublicationLease);
        fixture.Sources.TryApplyDirectoryPackagePublicationAsync(Arg.Any<IDirectoryPublicationLease>(),
                                                                  Arg.Any<string?>(),
                                                                  Arg.Any<DirectoryLibraryDefinition>(),
                                                                  Arg.Any<DateTime>(),
                                                                  Arg.Any<string>(),
                                                                  Arg.Any<CancellationToken>())
               .Returns(false);
        JobRecord? queuedJob = null;
        fixture.Jobs.UpsertAsync(Arg.Any<JobRecord>(), Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                queuedJob = call.ArgAt<JobRecord>(0);
                                return Task.CompletedTask;
                            });

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Importer.ImportAsync(
                                                                new ImportRequest
                                                                    {
                                                                        BundlePath = bundle,
                                                                        Overwrite = true
                                                                    },
                                                                progress: null,
                                                                TestContext.Current.CancellationToken));

        JobRecord persistedJob = Assert.IsType<JobRecord>(queuedJob);
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                         LibraryId,
                                                         Version,
                                                         publicationLease,
                                                         fixture.ModeLease,
                                                         Arg.Any<CancellationToken>());
        await fixture.Jobs.DidNotReceive()
                     .DeleteAsync(persistedJob.Id, Arg.Any<CancellationToken>());
        fixture.ReembedDispatcher.Received(requiredNumberOfCalls: 1)
               .TryDispatchPersisted(persistedJob);
        await fixture.Libraries.Received(requiredNumberOfCalls: 1)
                     .UpsertLibraryAsync(Arg.Any<LibraryRecord>(),
                                         Arg.Any<CancellationToken>());
        await fixture.Sources.Received(requiredNumberOfCalls: 1)
                     .TryApplyDirectoryPackagePublicationAsync(fixture.PublicationLease!,
                                                               Arg.Any<string?>(),
                                                               Arg.Any<DirectoryLibraryDefinition>(),
                                                               Arg.Any<DateTime>(),
                                                               Version,
                                                               Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConflictingImmutableSubjectCatalogIsRejectedBeforeModeAcquisition()
    {
        string bundle = CreateSubjectBundle(invalidPrimary: false, invalidSecondary: false);
        ImportFixture fixture = Fixture(existingDefinition: null);
        SubjectCatalogRecord conflicting = SubjectCatalog() with
                                               {
                                                   Concepts =
                                                   [
                                                       new SubjectConcept
                                                           {
                                                               Id = PrimarySubjectId,
                                                               Label = "Different label",
                                                               Description = "Different immutable catalog"
                                                           }
                                                   ]
                                               };
        fixture.Catalogs.GetManyAsync(Arg.Any<IReadOnlyCollection<SubjectCatalogKey>>(),
                                      Arg.Any<CancellationToken>())
               .Returns([conflicting]);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Contains("conflicts with the receiver's immutable catalog", exception.Message,
                        StringComparison.Ordinal);
        await AssertNoPurgeOrWritesAsync(fixture);
    }

    [Fact]
    public async Task SubjectCatalogConflictAppearingUnderModeLeaseStopsBeforeWrites()
    {
        string bundle = CreateSubjectBundle();
        ImportFixture fixture = Fixture(existingDefinition: null);
        SubjectCatalogRecord conflicting = SubjectCatalog() with
                                               {
                                                   Concepts =
                                                   [
                                                       new SubjectConcept
                                                           {
                                                               Id = PrimarySubjectId,
                                                               Label = "Different label",
                                                               Description = "Different immutable catalog"
                                                           }
                                                   ]
                                               };
        var catalogReads = 0;
        fixture.Catalogs.GetManyAsync(Arg.Any<IReadOnlyCollection<SubjectCatalogKey>>(),
                                      Arg.Any<CancellationToken>())
               .Returns(_ => ++catalogReads == 1 ? [] : [conflicting]);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Contains("conflicts with the receiver's immutable catalog", exception.Message,
                        StringComparison.Ordinal);
        Assert.Equal(2, catalogReads);
        await fixture.ModeManager.Received(requiredNumberOfCalls: 1)
                     .TryAcquireAsync(Arg.Any<string?>(),
                                      LibraryId,
                                      LibraryIngestionMode.Directory,
                                      Arg.Any<CancellationToken>());
        await fixture.ModeLease.DidNotReceive()
                     .TryCommitAsync(Arg.Any<CancellationToken>());
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteScanCandidateUnderLeaseAsync(default,
                                                         default!,
                                                         default!,
                                                         default!,
                                                         default!,
                                                         TestContext.Current.CancellationToken);
        await fixture.Libraries.DidNotReceive()
                     .UpsertVersionAsync(Arg.Any<LibraryVersionRecord>(), Arg.Any<CancellationToken>());
        await fixture.Catalogs.DidNotReceive()
                     .InsertRevisionAsync(Arg.Any<SubjectCatalogRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VersionCatalogWithoutAssignmentsIsPersistedExactlyOnce()
    {
        string bundle = CreateSubjectBundle(includeAssignment: false);
        ImportFixture fixture = Fixture(existingDefinition: null);

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.Equal([Version], result.VersionsImported);
        Assert.Empty(result.PartialFailures);
        await fixture.Catalogs.Received(requiredNumberOfCalls: 1)
                     .GetAsync(LibraryId, SubjectTaxonomyVersion, Arg.Any<CancellationToken>());
        await fixture.Catalogs.Received(requiredNumberOfCalls: 1)
                     .InsertRevisionAsync(Arg.Is<SubjectCatalogRecord>(catalog => catalog != null &&
                                              catalog.LibraryId == LibraryId &&
                                              catalog.TaxonomyVersion == SubjectTaxonomyVersion &&
                                              catalog.PublicationState ==
                                              SubjectCatalogPublicationState.Candidate),
                                          Arg.Any<CancellationToken>());
        await fixture.Catalogs.Received(requiredNumberOfCalls: 1)
                     .TryPublishImportCandidateAsync(LibraryId,
                                                     SubjectTaxonomyVersion,
                                                     Arg.Any<string>(),
                                                     Arg.Any<CancellationToken>());
        await fixture.Assignments.DidNotReceive()
                     .PersistAsync(Arg.Any<SubjectAssignmentRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportedVersionAndCatalogPublishOnlyAfterDependentWritesComplete()
    {
        string bundle = CreateSubjectBundle();
        ImportFixture fixture = Fixture(existingDefinition: null);
        var events = new List<string>();
        var versionRows = new Dictionary<string, LibraryVersionRecord>(StringComparer.Ordinal);
        LibraryVersionRecord? buildingVersion = null;
        LibraryVersionRecord? publishedVersion = null;
        SubjectCatalogRecord? candidateCatalog = null;
        string? operationId = null;
        fixture.Libraries.TryClaimImportVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                                     Arg.Any<string>(),
                                                     Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                buildingVersion = call.ArgAt<LibraryVersionRecord>(0);
                                operationId = call.ArgAt<string>(1);
                                versionRows[buildingVersion.Version] = buildingVersion;
                                events.Add("version-building");
                                return true;
                            });
        fixture.Libraries.GetVersionsAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(_ => versionRows.Values.ToList());
        fixture.Libraries.GetVersionAsync(LibraryId, Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(call => versionRows.GetValueOrDefault(call.ArgAt<string>(1)));
        fixture.Sources.PersistRevisionAsync(Arg.Any<DocumentRevisionRecord>(),
                                              Arg.Any<Stream>(),
                                              Arg.Any<Stream?>(),
                                              Arg.Any<CancellationToken>())
               .Returns(_ =>
                            {
                                events.Add("revision");
                                return Task.CompletedTask;
                            });
        fixture.Catalogs.InsertRevisionAsync(Arg.Any<SubjectCatalogRecord>(),
                                              Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                candidateCatalog = call.ArgAt<SubjectCatalogRecord>(0);
                                events.Add("catalog-candidate");
                                return Task.CompletedTask;
                            });
        fixture.Assignments.PersistAsync(Arg.Any<SubjectAssignmentRecord>(),
                                          Arg.Any<CancellationToken>())
               .Returns(_ =>
                            {
                                events.Add("assignment");
                                return Task.CompletedTask;
                            });
        fixture.Catalogs.TryPublishImportCandidateAsync(LibraryId,
                                                         SubjectTaxonomyVersion,
                                                         Arg.Any<string>(),
                                                         Arg.Any<CancellationToken>())
               .Returns(_ =>
                            {
                                events.Add("catalog-published");
                                return true;
                            });
        fixture.Libraries.TryPublishImportVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                                       Arg.Any<string>(),
                                                       Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                publishedVersion = call.ArgAt<LibraryVersionRecord>(0);
                                versionRows[publishedVersion.Version] = publishedVersion;
                                events.Add("version-published");
                                return true;
                            });

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.Equal([Version], result.VersionsImported);
        Assert.Equal(["version-building", "revision", "catalog-candidate", "assignment",
                      "catalog-published", "version-published"],
                     events);
        LibraryVersionRecord building = Assert.IsType<LibraryVersionRecord>(buildingVersion);
        LibraryVersionRecord published = Assert.IsType<LibraryVersionRecord>(publishedVersion);
        SubjectCatalogRecord candidate = Assert.IsType<SubjectCatalogRecord>(candidateCatalog);
        Assert.Equal(VersionPublicationState.Building, building.PublicationState);
        Assert.Equal(VersionPublicationState.Published, published.PublicationState);
        Assert.Equal(SubjectCatalogPublicationState.Candidate, candidate.PublicationState);
        Assert.Equal(SubjectScanRunId, building.ScanRunId);
        Assert.Equal(SubjectScanRunId, published.ScanRunId);
        Assert.Equal(SubjectScanRunId, candidate.ScanRunId);
        Assert.Equal(operationId, building.ImportOperationId);
        Assert.Equal(operationId, published.ImportOperationId);
        Assert.Equal(operationId, candidate.ImportOperationId);
        await fixture.Libraries.DidNotReceive()
                     .UpsertVersionAsync(Arg.Any<LibraryVersionRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailureAfterCatalogInsertionRemovesOnlyOwnedCandidate()
    {
        string bundle = CreateSubjectBundle();
        ImportFixture fixture = Fixture(ExistingDefinition(PreviousVersion), PreviousLibrary());
        SubjectCatalogRecord? insertedCatalog = null;
        fixture.Catalogs.InsertRevisionAsync(Arg.Any<SubjectCatalogRecord>(),
                                              Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                insertedCatalog = call.ArgAt<SubjectCatalogRecord>(0);
                                return Task.CompletedTask;
                            });
        fixture.Assignments.PersistAsync(Arg.Any<SubjectAssignmentRecord>(),
                                          Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException(new IOException("forced assignment failure")));

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.Single(result.PartialFailures);
        SubjectCatalogRecord candidate = Assert.IsType<SubjectCatalogRecord>(insertedCatalog);
        string operationId = Assert.IsType<string>(candidate.ImportOperationId);
        Assert.Equal(SubjectCatalogPublicationState.Candidate, candidate.PublicationState);
        await fixture.Catalogs.DidNotReceiveWithAnyArgs()
                     .TryPublishImportCandidateAsync(default!,
                                                     default!,
                                                     default!,
                                                     TestContext.Current.CancellationToken);
        await fixture.Catalogs.Received(requiredNumberOfCalls: 1)
                     .DeleteImportCandidateIfUnreferencedAsync(LibraryId,
                                                               SubjectTaxonomyVersion,
                                                               operationId,
                                                               Version,
                                                               Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AppliedThenThrowCatalogInsertIsRecoveredAsOwnedCandidate()
    {
        string bundle = CreateSubjectBundle();
        ImportFixture fixture = Fixture(ExistingDefinition(PreviousVersion), PreviousLibrary());
        SubjectCatalogRecord? durableCatalog = null;
        var catalogReads = 0;
        fixture.Catalogs.GetAsync(LibraryId,
                                  SubjectTaxonomyVersion,
                                  Arg.Any<CancellationToken>())
               .Returns(_ => ++catalogReads == 1 ? null : durableCatalog);
        fixture.Catalogs.InsertRevisionAsync(Arg.Any<SubjectCatalogRecord>(),
                                              Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                durableCatalog = call.ArgAt<SubjectCatalogRecord>(0);
                                return Task.FromException(new IOException("ambiguous catalog insert"));
                            });

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.Single(result.PartialFailures);
        SubjectCatalogRecord candidate = Assert.IsType<SubjectCatalogRecord>(durableCatalog);
        string operationId = Assert.IsType<string>(candidate.ImportOperationId);
        await fixture.Catalogs.Received(requiredNumberOfCalls: 1)
                     .DeleteImportCandidateIfUnreferencedAsync(LibraryId,
                                                               SubjectTaxonomyVersion,
                                                               operationId,
                                                               Version,
                                                               Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForeignCatalogReadbackAfterInsertFailureSkipsBroadCleanup()
    {
        string bundle = CreateSubjectBundle();
        ImportFixture fixture = Fixture(existingDefinition: null);
        SubjectCatalogRecord? attemptedCatalog = null;
        var catalogReads = 0;
        fixture.Catalogs.GetAsync(LibraryId,
                                  SubjectTaxonomyVersion,
                                  Arg.Any<CancellationToken>())
               .Returns(_ => ++catalogReads == 1
                                 ? null
                                 : SubjectCatalog() with { ImportOperationId = "foreign-import" });
        fixture.Catalogs.InsertRevisionAsync(Arg.Any<SubjectCatalogRecord>(),
                                              Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                attemptedCatalog = call.ArgAt<SubjectCatalogRecord>(0);
                                return Task.FromException(new IOException("ambiguous catalog insert"));
                            });

        InvalidOperationException exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Contains("not attributable", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(attemptedCatalog);
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteLibraryUnderModeLeaseAsync(default,
                                                       default!,
                                                       default!,
                                                       TestContext.Current.CancellationToken);
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteScanCandidateUnderLeaseAsync(default,
                                                         default!,
                                                         default!,
                                                         default!,
                                                         default!,
                                                         TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PreexistingEquivalentCatalogIsNeverMutatedByImportLifecycle()
    {
        string bundle = CreateSubjectBundle();
        ImportFixture fixture = Fixture(existingDefinition: null);
        SubjectCatalogRecord existingCatalog = SubjectCatalog();
        fixture.Catalogs.GetManyAsync(Arg.Any<IReadOnlyCollection<SubjectCatalogKey>>(),
                                      Arg.Any<CancellationToken>())
               .Returns([existingCatalog]);
        fixture.Catalogs.GetAsync(LibraryId,
                                  SubjectTaxonomyVersion,
                                  Arg.Any<CancellationToken>())
               .Returns(existingCatalog);

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.Equal([Version], result.VersionsImported);
        await fixture.Catalogs.DidNotReceive()
                     .InsertRevisionAsync(Arg.Any<SubjectCatalogRecord>(), Arg.Any<CancellationToken>());
        await fixture.Catalogs.DidNotReceiveWithAnyArgs()
                     .TryPublishImportCandidateAsync(default!,
                                                     default!,
                                                     default!,
                                                     TestContext.Current.CancellationToken);
        await fixture.Catalogs.DidNotReceiveWithAnyArgs()
                     .TryRollbackImportCandidatePublicationAsync(default!,
                                                                 default!,
                                                                 default!,
                                                                 TestContext.Current.CancellationToken);
        await fixture.Catalogs.DidNotReceiveWithAnyArgs()
                     .DeleteImportCandidateIfUnreferencedAsync(default!,
                                                               default!,
                                                               default!,
                                                               default!,
                                                               TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PublishedCatalogReferencedBySurvivorIsNotDeletedDuringImportRollback()
    {
        string bundle = CreateSubjectBundle();
        ImportFixture fixture = Fixture(ExistingDefinition(PreviousVersion), PreviousLibrary());
        fixture.Libraries.UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException(new IOException("forced summary failure")));
        fixture.Catalogs.TryRollbackImportCandidatePublicationIfUnreferencedAsync(
                    LibraryId,
                    SubjectTaxonomyVersion,
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
               .Returns(ImportCatalogRollbackOutcome.ReferencedBySurvivor);

        await Assert.ThrowsAsync<IOException>(() => fixture.Importer.ImportAsync(
                                                  new ImportRequest { BundlePath = bundle },
                                                  progress: null,
                                                  TestContext.Current.CancellationToken));

        await fixture.Catalogs.Received(requiredNumberOfCalls: 1)
                     .TryRollbackImportCandidatePublicationIfUnreferencedAsync(
                         LibraryId,
                         SubjectTaxonomyVersion,
                         Arg.Any<string>(),
                         Arg.Any<CancellationToken>());
        await fixture.Catalogs.DidNotReceiveWithAnyArgs()
                     .DeleteImportCandidateIfUnreferencedAsync(default!,
                                                               default!,
                                                               default!,
                                                               default!,
                                                               TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RetainedVersionAfterDeletionFailureKeepsPublishedCatalog()
    {
        string bundle = CreateSubjectBundle();
        ImportFixture fixture = Fixture(ExistingDefinition(PreviousVersion), PreviousLibrary());
        fixture.Libraries.UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException(new IOException("forced summary failure")));
        fixture.Deletion.DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                             LibraryId,
                                                             Version,
                                                             fixture.PublicationLease!,
                                                             fixture.ModeLease,
                                                             Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<LibraryDeletionResult>(
                            new IOException("forced version deletion failure")));
        fixture.Catalogs.TryRollbackImportCandidatePublicationIfUnreferencedAsync(
                    LibraryId,
                    SubjectTaxonomyVersion,
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
               .Returns(ImportCatalogRollbackOutcome.ReferencedBySurvivor);

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Contains(exception.Flatten().InnerExceptions,
                        failure => failure.Message == "forced version deletion failure");
        Assert.Contains(exception.Flatten().InnerExceptions,
                        failure => failure.Message.Contains("remained after cleanup", StringComparison.Ordinal));
        await fixture.Catalogs.Received(requiredNumberOfCalls: 1)
                     .TryRollbackImportCandidatePublicationIfUnreferencedAsync(
                         LibraryId,
                         SubjectTaxonomyVersion,
                         Arg.Any<string>(),
                         Arg.Any<CancellationToken>());
        await fixture.Catalogs.DidNotReceiveWithAnyArgs()
                     .DeleteImportCandidateIfUnreferencedAsync(default!,
                                                               default!,
                                                               default!,
                                                               default!,
                                                               TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AppliedThenThrowVersionClaimIsRecoveredAsOwnedBuildingRow()
    {
        string bundle = CreateValidBundle();
        ImportFixture fixture = Fixture(ExistingDefinition(PreviousVersion), PreviousLibrary());
        LibraryVersionRecord? durableVersion = null;
        var versionDeleted = false;
        fixture.Libraries.TryClaimImportVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                                     Arg.Any<string>(),
                                                     Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                durableVersion = call.ArgAt<LibraryVersionRecord>(0);
                                return Task.FromException<bool>(new IOException("ambiguous claim"));
                            });
        fixture.Libraries.GetVersionAsync(LibraryId, Version, Arg.Any<CancellationToken>())
               .Returns(_ => versionDeleted ? null : durableVersion);
        fixture.Deletion.DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                             LibraryId,
                                                             Version,
                                                             fixture.PublicationLease!,
                                                             fixture.ModeLease,
                                                             Arg.Any<CancellationToken>())
               .Returns(_ =>
                            {
                                versionDeleted = true;
                                return EmptyDeletionResult;
                            });

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.Single(result.PartialFailures);
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteScanCandidateUnderLeaseAsync(profile: null,
                                                         LibraryId,
                                                         Version,
                                                         fixture.PublicationLease!,
                                                         fixture.ModeLease,
                                                         Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AppliedThenThrowVersionPublicationIsConfirmedAsSuccess()
    {
        string bundle = CreateValidBundle();
        ImportFixture fixture = Fixture(existingDefinition: null);
        LibraryVersionRecord? durableVersion = null;
        fixture.Libraries.TryPublishImportVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                                       Arg.Any<string>(),
                                                       Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                durableVersion = call.ArgAt<LibraryVersionRecord>(0);
                                return Task.FromException<bool>(new IOException("ambiguous publication"));
                            });
        fixture.Libraries.GetVersionAsync(LibraryId, Version, Arg.Any<CancellationToken>())
               .Returns(_ => durableVersion ?? VersionRecord());

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.Equal([Version], result.VersionsImported);
        LibraryVersionRecord published = Assert.IsType<LibraryVersionRecord>(durableVersion);
        Assert.Equal(VersionPublicationState.Published, published.PublicationState);
        Assert.False(string.IsNullOrWhiteSpace(published.ImportOperationId));
    }

    [Fact]
    public async Task AssignmentTaxonomyMismatchIsRejectedBeforeModeAcquisition()
    {
        string bundle = CreateSubjectBundle(assignmentTaxonomy: "taxonomy-other");
        ImportFixture fixture = Fixture(existingDefinition: null);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Contains("taxonomy does not match its library version", exception.Message,
                        StringComparison.Ordinal);
        await AssertNoPurgeOrWritesAsync(fixture);
    }

    [Fact]
    public async Task AssignmentWithoutVersionTaxonomyIsRejectedBeforeModeAcquisition()
    {
        string bundle = CreateSubjectBundle(versionTaxonomy: null);
        ImportFixture fixture = Fixture(existingDefinition: null);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Contains("subject assignments is missing its taxonomy", exception.Message,
                        StringComparison.Ordinal);
        await AssertNoPurgeOrWritesAsync(fixture);
    }

    [Fact]
    public async Task MissingVersionCatalogIsRejectedBeforeModeAcquisition()
    {
        string bundle = CreateSubjectBundle(includeAssignment: false, includeCatalog: false);
        ImportFixture fixture = Fixture(existingDefinition: null);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Contains("Missing subject catalog", exception.Message, StringComparison.Ordinal);
        await AssertNoPurgeOrWritesAsync(fixture);
    }

    [Theory]
    [InlineData(InvalidSubjectCatalogShape.EmptyCatalog)]
    [InlineData(InvalidSubjectCatalogShape.NullConceptList)]
    [InlineData(InvalidSubjectCatalogShape.NullConcept)]
    [InlineData(InvalidSubjectCatalogShape.MissingLabel)]
    [InlineData(InvalidSubjectCatalogShape.MissingDescription)]
    [InlineData(InvalidSubjectCatalogShape.NullAliasList)]
    [InlineData(InvalidSubjectCatalogShape.BlankAlias)]
    [InlineData(InvalidSubjectCatalogShape.DuplicateAlias)]
    [InlineData(InvalidSubjectCatalogShape.CandidateCatalog)]
    public async Task InvalidSubjectCatalogShapeIsRejectedBeforeModeAcquisition(
        InvalidSubjectCatalogShape invalidShape)
    {
        string bundle = CreateSubjectBundle(includeAssignment: false, invalidCatalogShape: invalidShape);
        ImportFixture fixture = Fixture(existingDefinition: null);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Contains("Subject catalog", exception.Message, StringComparison.Ordinal);
        await AssertNoPurgeOrWritesAsync(fixture);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DanglingAssignmentSubjectIsRejectedBeforeModeAcquisition(bool invalidPrimary,
                                                                               bool invalidSecondary)
    {
        string bundle = CreateSubjectBundle(invalidPrimary, invalidSecondary);
        ImportFixture fixture = Fixture(existingDefinition: null);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Contains("subject outside catalog", exception.Message, StringComparison.Ordinal);
        await AssertNoPurgeOrWritesAsync(fixture);
    }

    [Fact]
    public async Task LegacyImporterRejectsDocumentLifecyclePackageBeforeModeAcquisition()
    {
        string bundle = CreateSubjectBundle();
        ImportFixture fixture = Fixture(existingDefinition: null, includeDocumentRepositories: false);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Contains("without document repositories", exception.Message, StringComparison.Ordinal);
        await fixture.ModeManager.DidNotReceiveWithAnyArgs()
                     .TryAcquireAsync(default,
                                      default!,
                                      default,
                                      TestContext.Current.CancellationToken);
        await AssertNoPurgeOrWritesAsync(fixture);
    }

    [Fact]
    public async Task LegacyImporterAcceptsPackageWithoutDocumentLifecycleData()
    {
        string bundle = CreateValidBundle(isDirectory: false);
        ImportFixture fixture = NewWebFixture(includeDocumentRepositories: false);

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.Equal([Version], result.VersionsImported);
        Assert.Empty(result.PartialFailures);
    }

    [Fact]
    public async Task WebLibrarySummaryAppliedThenThrowIsConfirmedWithoutCleanup()
    {
        string bundle = CreateValidBundle(isDirectory: false);
        ImportFixture fixture = NewWebFixture();
        LibraryRecord? durableSummary = null;
        var summaryReads = 0;
        fixture.Libraries.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(_ => ++summaryReads < 3 ? null : durableSummary);
        fixture.Libraries.UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                durableSummary = call.ArgAt<LibraryRecord>(0);
                                return Task.FromException(
                                    new IOException("summary acknowledgement lost"));
                            });

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.Equal([Version], result.VersionsImported);
        Assert.Empty(result.PartialFailures);
        Assert.Equal(3, summaryReads);
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteLibraryUnderModeLeaseAsync(default,
                                                       default!,
                                                       default!,
                                                       TestContext.Current.CancellationToken);
        await fixture.ModeLease.DidNotReceiveWithAnyArgs()
                     .TryDeleteOwnershipAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WebLibrarySummaryNoWriteRollsBackExistingImportUnderHeldModeLease()
    {
        string bundle = CreateValidBundle(isDirectory: false);
        LibraryRecord previousLibrary = PreviousLibrary();
        ImportFixture fixture = ExistingWebFixture(previousLibrary);
        fixture.Libraries.UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException(new IOException("forced summary failure")));

        IOException exception = await Assert.ThrowsAsync<IOException>(() => fixture.Importer.ImportAsync(
                                                                          new ImportRequest { BundlePath = bundle },
                                                                          progress: null,
                                                                          TestContext.Current.CancellationToken));

        Assert.Equal("forced summary failure", exception.Message);
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteVersionUnderModeLeaseAsync(profile: null,
                                                       LibraryId,
                                                       Version,
                                                       fixture.ModeLease,
                                                       Arg.Any<CancellationToken>());
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteLibraryUnderModeLeaseAsync(default,
                                                       default!,
                                                       default!,
                                                       TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReservedModeForLegacyWebLibraryUsesGranularRollbackAfterCommit()
    {
        string bundle = CreateValidBundle(isDirectory: false);
        ImportFixture fixture = ExistingWebFixture(PreviousLibrary());
        fixture.ModeLease.OwnershipStateAtAcquisition.Returns(
            LibraryIngestionOwnershipState.Reserved);
        fixture.Libraries.UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException(new IOException("forced summary failure")));

        await Assert.ThrowsAsync<IOException>(() => fixture.Importer.ImportAsync(
                                                  new ImportRequest { BundlePath = bundle },
                                                  progress: null,
                                                  TestContext.Current.CancellationToken));

        await fixture.ModeLease.Received(requiredNumberOfCalls: 1)
                     .TryCommitAsync(Arg.Any<CancellationToken>());
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteVersionUnderModeLeaseAsync(profile: null,
                                                       LibraryId,
                                                       Version,
                                                       fixture.ModeLease,
                                                       Arg.Any<CancellationToken>());
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteLibraryUnderModeLeaseAsync(default,
                                                       default!,
                                                       default!,
                                                       TestContext.Current.CancellationToken);
        await fixture.ModeLease.DidNotReceiveWithAnyArgs()
                     .TryDeleteOwnershipAsync(TestContext.Current.CancellationToken);
        await fixture.ModeLease.DidNotReceiveWithAnyArgs()
                     .TryAbandonReservationAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReservedWebModeWithOperationalHistoryNeverUsesWholeLibraryCleanup()
    {
        string bundle = CreateValidBundle(isDirectory: false);
        ImportFixture fixture = NewWebFixture();
        fixture.Modes.GetLibraryDataEvidenceAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(new LibraryIngestionDataEvidence(false, false, false, false, true));
        fixture.Libraries.UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException(new IOException("forced summary failure")));

        await Assert.ThrowsAsync<IOException>(() => fixture.Importer.ImportAsync(
                                                  new ImportRequest { BundlePath = bundle },
                                                  progress: null,
                                                  TestContext.Current.CancellationToken));

        await fixture.ModeLease.Received(requiredNumberOfCalls: 1)
                     .TryCommitAsync(Arg.Any<CancellationToken>());
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteVersionUnderModeLeaseAsync(profile: null,
                                                       LibraryId,
                                                       Version,
                                                       fixture.ModeLease,
                                                       Arg.Any<CancellationToken>());
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteLibraryUnderModeLeaseAsync(default,
                                                       default!,
                                                       default!,
                                                       TestContext.Current.CancellationToken);
        await fixture.ModeLease.DidNotReceiveWithAnyArgs()
                     .TryDeleteOwnershipAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WebLibrarySummaryNoWriteDeletesNewLibraryAndOwnership()
    {
        string bundle = CreateValidBundle(isDirectory: false);
        ImportFixture fixture = NewWebFixture();
        fixture.Libraries.UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException(new IOException("forced summary failure")));

        await Assert.ThrowsAsync<IOException>(() => fixture.Importer.ImportAsync(
                                                  new ImportRequest { BundlePath = bundle },
                                                  progress: null,
                                                  TestContext.Current.CancellationToken));

        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteLibraryUnderModeLeaseAsync(profile: null,
                                                       LibraryId,
                                                       fixture.ModeLease,
                                                       Arg.Any<CancellationToken>());
        await fixture.ModeLease.Received(requiredNumberOfCalls: 1)
                     .TryDeleteOwnershipAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnattributableWebSummaryOutcomeSkipsDestructiveCleanup(bool confirmationReadFails)
    {
        string bundle = CreateValidBundle(isDirectory: false);
        ImportFixture fixture = NewWebFixture();
        var summaryReads = 0;
        var foreign = new LibraryRecord
                          {
                              Id = LibraryId,
                              Name = "Foreign summary",
                              Hint = "Foreign summary",
                              CurrentVersion = ForeignVersion,
                              AllVersions = [ForeignVersion]
                          };
        fixture.Libraries.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(_ =>
                            {
                                summaryReads++;
                                if (summaryReads < 3)
                                    return Task.FromResult<LibraryRecord?>(null);
                                return confirmationReadFails
                                           ? Task.FromException<LibraryRecord?>(
                                               new IOException("summary confirmation failed"))
                                           : Task.FromResult<LibraryRecord?>(foreign);
                            });
        fixture.Libraries.UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException(new IOException("summary acknowledgement lost")));

        InvalidOperationException exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Contains("outcome", exception.Message, StringComparison.OrdinalIgnoreCase);
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteLibraryUnderModeLeaseAsync(default,
                                                       default!,
                                                       default!,
                                                       TestContext.Current.CancellationToken);
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteVersionUnderModeLeaseAsync(default,
                                                       default!,
                                                       default!,
                                                       default!,
                                                       TestContext.Current.CancellationToken);
        await fixture.ModeLease.DidNotReceiveWithAnyArgs()
                     .TryDeleteOwnershipAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReembedJobAppliedThenThrowIsConfirmedAtMongoPrecisionAndDispatchedAfterPublication()
    {
        string bundle = CreateValidBundle(isDirectory: false, encoderMatches: false);
        ImportFixture fixture = NewWebFixture();
        var events = new List<string>();
        JobRecord? durableJob = null;
        JobRecord? dispatchedJob = null;
        fixture.Libraries.UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>())
               .Returns(_ =>
                            {
                                events.Add("summary");
                                return Task.CompletedTask;
                            });
        fixture.Jobs.UpsertAsync(Arg.Any<JobRecord>(), Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                events.Add("job");
                                durableJob = MongoRoundTrip(call.ArgAt<JobRecord>(0));
                                return Task.FromException(new IOException("job acknowledgement lost"));
                            });
        fixture.Jobs.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(_ => durableJob);
        fixture.ReembedDispatcher.TryDispatchPersisted(Arg.Any<JobRecord>())
               .Returns(call =>
                            {
                                events.Add("dispatch");
                                dispatchedJob = call.ArgAt<JobRecord>(0);
                                return true;
                            });

        ImportResult result = await fixture.Importer.ImportAsync(
                                  new ImportRequest { BundlePath = bundle, Profile = "company" },
                                  progress: null,
                                  TestContext.Current.CancellationToken);

        JobRecord persisted = Assert.IsType<JobRecord>(durableJob);
        JobRecord dispatched = Assert.IsType<JobRecord>(dispatchedJob);
        Assert.Equal(0, persisted.CreatedAt.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal("company", persisted.Profile);
        Assert.Equal(persisted.Id, dispatched.Id);
        Assert.Equal([persisted.Id], result.PendingReembedJobIds);
        Assert.Equal(["summary", "job", "dispatch"], events);
        Assert.Empty(result.PartialFailures);
    }

    [Fact]
    public async Task MissingNewLibraryReembedJobWriteRemovesUnpublishedLibrary()
    {
        string bundle = CreateValidBundle(isDirectory: false, encoderMatches: false);
        ImportFixture fixture = NewWebFixture();
        fixture.Jobs.UpsertAsync(Arg.Any<JobRecord>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException(new IOException("forced job failure")));
        fixture.Jobs.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns((JobRecord?)null);

        await Assert.ThrowsAsync<IOException>(() => fixture.Importer.ImportAsync(
                                                  new ImportRequest { BundlePath = bundle },
                                                  progress: null,
                                                  TestContext.Current.CancellationToken));

        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteLibraryUnderModeLeaseAsync(profile: null,
                                                       LibraryId,
                                                       fixture.ModeLease,
                                                       Arg.Any<CancellationToken>());
        fixture.ReembedDispatcher.DidNotReceive().TryDispatchPersisted(Arg.Any<JobRecord>());
    }

    [Fact]
    public async Task MissingExistingLibraryReembedJobWriteRollsBackOnlyAttemptedVersion()
    {
        string bundle = CreateValidBundle(isDirectory: false, encoderMatches: false);
        ImportFixture fixture = ExistingWebFixture(PreviousLibrary());
        JobRecord? attemptedJob = null;
        fixture.Jobs.UpsertAsync(Arg.Any<JobRecord>(), Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                attemptedJob = call.ArgAt<JobRecord>(0);
                                return Task.FromException(new IOException("forced job failure"));
                            });
        fixture.Jobs.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns((JobRecord?)null);

        await Assert.ThrowsAsync<IOException>(() => fixture.Importer.ImportAsync(
                                                  new ImportRequest { BundlePath = bundle },
                                                  progress: null,
                                                  TestContext.Current.CancellationToken));

        JobRecord attempted = Assert.IsType<JobRecord>(attemptedJob);
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteVersionUnderModeLeaseAsync(profile: null,
                                                       LibraryId,
                                                       Version,
                                                       fixture.ModeLease,
                                                       Arg.Any<CancellationToken>());
        await fixture.Jobs.Received(requiredNumberOfCalls: 1)
                     .DeleteAsync(attempted.Id, Arg.Any<CancellationToken>());
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteLibraryUnderModeLeaseAsync(default,
                                                       default!,
                                                       default!,
                                                       TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnknownReembedJobWriteOutcomeSkipsDestructiveCleanup()
    {
        string bundle = CreateValidBundle(isDirectory: false, encoderMatches: false);
        ImportFixture fixture = NewWebFixture();
        fixture.Jobs.UpsertAsync(Arg.Any<JobRecord>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException(new IOException("job acknowledgement lost")));
        fixture.Jobs.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<JobRecord?>(new IOException("job confirmation failed")));

        InvalidOperationException exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            fixture.Importer.ImportAsync(new ImportRequest { BundlePath = bundle },
                                         progress: null,
                                         TestContext.Current.CancellationToken));

        Assert.Contains("durable outcome", exception.Message, StringComparison.Ordinal);
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteLibraryUnderModeLeaseAsync(default,
                                                       default!,
                                                       default!,
                                                       TestContext.Current.CancellationToken);
        await fixture.Deletion.DidNotReceiveWithAnyArgs()
                     .DeleteVersionUnderModeLeaseAsync(default,
                                                       default!,
                                                       default!,
                                                       default!,
                                                       TestContext.Current.CancellationToken);
        fixture.ReembedDispatcher.DidNotReceive().TryDispatchPersisted(Arg.Any<JobRecord>());
    }

    [Fact]
    public async Task OverwriteWithMissingReembedJobPreservesReplacementAndRequiresManualRecovery()
    {
        string bundle = CreateValidBundle(isDirectory: false, encoderMatches: false);
        ImportFixture fixture = WebFixture();
        fixture.Jobs.UpsertAsync(Arg.Any<JobRecord>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException(new IOException("forced job failure")));
        fixture.Jobs.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns((JobRecord?)null);

        ImportResult result = await fixture.Importer.ImportAsync(new ImportRequest
                                                                     {
                                                                         BundlePath = bundle,
                                                                         Overwrite = true
                                                                     },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.Equal([Version], result.VersionsImported);
        Assert.Equal([Version], result.OverwrittenVersions);
        Assert.Empty(result.PendingReembedJobIds);
        Assert.Single(result.PartialFailures);
        Assert.Contains("run reembed_library", result.RecommendedFollowUp, StringComparison.Ordinal);
        Assert.DoesNotContain("in progress", result.RecommendedFollowUp, StringComparison.OrdinalIgnoreCase);
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteVersionUnderModeLeaseAsync(profile: null,
                                                       LibraryId,
                                                       Version,
                                                       fixture.ModeLease,
                                                       Arg.Any<CancellationToken>());
        fixture.ReembedDispatcher.DidNotReceive().TryDispatchPersisted(Arg.Any<JobRecord>());
    }

    [Fact]
    public async Task OverwriteCompactionFailureStillDispatchesPreservedDurableReembedJob()
    {
        string bundle = CreateValidBundle(isDirectory: false, encoderMatches: false);
        var compactor = Substitute.For<ICollectionCompactor>();
        compactor.DefaultHotCollections.Returns(["chunks"]);
        compactor.CompactAsync(Arg.Any<IMongoDatabase>(),
                               Arg.Any<string>(),
                               Arg.Any<CancellationToken>())
                 .Returns(_ => Task.FromException<CompactResult>(
                              new IOException("forced compaction failure")));
        var database = Substitute.For<IMongoDatabase>();
        ImportFixture fixture = Fixture(existingDefinition: null,
                                        compactor: compactor,
                                        databaseResolver: _ => database);
        fixture.Libraries.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(PackagingFixtures.MakeLibrary(LibraryId, Version));
        ConfigureWebMode(fixture, ownsNewReservation: false);
        JobRecord? durableJob = null;
        JobRecord? dispatchedJob = null;
        fixture.Jobs.UpsertAsync(Arg.Any<JobRecord>(), Arg.Any<CancellationToken>())
               .Returns(call =>
                            {
                                durableJob = call.ArgAt<JobRecord>(0);
                                return Task.CompletedTask;
                            });
        fixture.ReembedDispatcher.TryDispatchPersisted(Arg.Any<JobRecord>())
               .Returns(call =>
                            {
                                dispatchedJob = call.ArgAt<JobRecord>(0);
                                return true;
                            });

        IOException exception = await Assert.ThrowsAsync<IOException>(() => fixture.Importer.ImportAsync(
                                                                          new ImportRequest
                                                                              {
                                                                                  BundlePath = bundle,
                                                                                  Overwrite = true,
                                                                                  Compact = true
                                                                              },
                                                                          progress: null,
                                                                          TestContext.Current.CancellationToken));

        Assert.Equal("forced compaction failure", exception.Message);
        Assert.Same(Assert.IsType<JobRecord>(durableJob), Assert.IsType<JobRecord>(dispatchedJob));
        await compactor.Received(requiredNumberOfCalls: 1)
                       .CompactAsync(database, "chunks", Arg.Any<CancellationToken>());
        await fixture.Deletion.Received(requiredNumberOfCalls: 1)
                     .DeleteVersionUnderModeLeaseAsync(profile: null,
                                                       LibraryId,
                                                       Version,
                                                       fixture.ModeLease,
                                                       Arg.Any<CancellationToken>());
        await fixture.Jobs.DidNotReceiveWithAnyArgs()
                     .DeleteAsync(default!, TestContext.Current.CancellationToken);
    }

    private ImportFixture Fixture(DirectoryLibraryDefinition? existingDefinition,
                                   LibraryRecord? existingLibrary = null,
                                   bool includeDocumentRepositories = true,
                                   ICollectionCompactor? compactor = null,
                                   Func<string?, IMongoDatabase>? databaseResolver = null)
    {
        var libraries = Substitute.For<ILibraryRepository>();
        var jobs = Substitute.For<IJobRepository>();
        var embedding = Substitute.For<IEmbeddingProvider>();
        var sources = Substitute.For<ISourceDocumentRepository>();
        var catalogs = Substitute.For<ISubjectCatalogRepository>();
        var assignments = Substitute.For<ISubjectAssignmentRepository>();
        var profiles = Substitute.For<ILibraryProfileRepository>();
        var indexes = Substitute.For<ILibraryIndexRepository>();
        var excluded = Substitute.For<IExcludedSymbolsRepository>();
        var diffs = Substitute.For<IDiffRepository>();
        var pages = Substitute.For<IPageRepository>();
        var chunks = Substitute.For<IChunkRepository>();
        var bm25 = Substitute.For<IBm25ShardRepository>();
        var deletion = Substitute.For<ILibraryDeletionService>();
        var modeManager = Substitute.For<ILibraryIngestionModeLeaseManager>();
        var modes = Substitute.For<ILibraryIngestionModeRepository>();
        var modeLease = Substitute.For<ILibraryIngestionModeLease>();
        var reembedDispatcher = Substitute.For<IReembedJobDispatcher>();
        IDirectoryPublicationLease? publicationLease = existingDefinition == null
                                                                  ? null
                                                                  : Substitute.For<IDirectoryPublicationLease>();
        LibraryRecord? effectiveLibrary = existingLibrary ??
                                          (existingDefinition == null
                                               ? null
                                               : PackagingFixtures.MakeLibrary(LibraryId, Version));
        var versionRows = (effectiveLibrary?.AllVersions ?? [])
            .ToDictionary(version => version,
                          version => string.Equals(version, Version, StringComparison.Ordinal)
                                         ? VersionRecord()
                                         : PackagingFixtures.MakeVersion(
                                             LibraryId,
                                             version,
                                             pageCount: 0,
                                             chunkCount: 0,
                                             dim: PackagingFixtures.DefaultDim),
                          StringComparer.Ordinal);
        DirectoryLibraryDefinition? leasedDefinition = existingDefinition == null
                                                          ? null
                                                          : existingDefinition with
                                                              {
                                                                  PublicationLeaseScanRunId = "package-import",
                                                                  PublicationLeaseRegistrationRevision =
                                                                      existingDefinition.RegistrationRevision,
                                                                  PublicationLeaseExpiresAtUtc =
                                                                      DateTime.UtcNow.AddMinutes(5)
                                                              };
        libraries.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
                 .Returns(effectiveLibrary);
        libraries.GetVersionsAsync(LibraryId, Arg.Any<CancellationToken>())
                 .Returns(_ => versionRows.Values.ToList());
        libraries.GetVersionAsync(LibraryId, Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(call => versionRows.GetValueOrDefault(call.ArgAt<string>(1)));
        libraries.TryClaimImportVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                              Arg.Any<string>(),
                                              Arg.Any<CancellationToken>())
                 .Returns(call =>
                              {
                                  LibraryVersionRecord claimed = call.ArgAt<LibraryVersionRecord>(0);
                                  versionRows[claimed.Version] = claimed;
                                  return true;
                              });
        libraries.TryPublishImportVersionAsync(Arg.Any<LibraryVersionRecord>(),
                                                Arg.Any<string>(),
                                                Arg.Any<CancellationToken>())
                 .Returns(call =>
                              {
                                  LibraryVersionRecord published = call.ArgAt<LibraryVersionRecord>(0);
                                  versionRows[published.Version] = published;
                                  return true;
                              });
        libraries.TryReplaceLibrarySummaryAsync(Arg.Any<LibraryRecord>(),
                                                 Arg.Any<LibraryRecord>(),
                                                 Arg.Any<CancellationToken>())
                 .Returns(true);
        libraries.TryDeleteLibrarySummaryAsync(Arg.Any<LibraryRecord>(),
                                                Arg.Any<CancellationToken>())
                 .Returns(true);
        catalogs.TryPublishImportCandidateAsync(Arg.Any<string>(),
                                                 Arg.Any<string>(),
                                                 Arg.Any<string>(),
                                                 Arg.Any<CancellationToken>())
                .Returns(true);
        catalogs.TryRollbackImportCandidatePublicationAsync(Arg.Any<string>(),
                                                             Arg.Any<string>(),
                                                             Arg.Any<string>(),
                                                             Arg.Any<CancellationToken>())
                .Returns(true);
        catalogs.TryRollbackImportCandidatePublicationIfUnreferencedAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(ImportCatalogRollbackOutcome.RolledBack);
        catalogs.DeleteImportCandidateIfUnreferencedAsync(Arg.Any<string>(),
                                                           Arg.Any<string>(),
                                                           Arg.Any<string>(),
                                                           Arg.Any<string>(),
                                                           Arg.Any<CancellationToken>())
                .Returns(true);
        jobs.ListActiveAsync(Arg.Any<string>(),
                             Arg.Any<string?>(),
                             Arg.Any<JobType?>(),
                             Arg.Any<CancellationToken>())
            .Returns(Array.Empty<JobRecord>());
        embedding.ProviderId.Returns("onnx-local");
        embedding.ModelName.Returns("test-embed");
        embedding.Dimensions.Returns(PackagingFixtures.DefaultDim);
        var definitionReads = 0;
        sources.GetDirectoryDefinitionAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(_ => existingDefinition == null || ++definitionReads == 1
                                 ? existingDefinition
                                 : leasedDefinition);
        sources.TryApplyDirectoryPackagePublicationAsync(Arg.Any<IDirectoryPublicationLease>(),
                                                          Arg.Any<string?>(),
                                                          Arg.Any<DirectoryLibraryDefinition>(),
                                                          Arg.Any<DateTime>(),
                                                          Arg.Any<string>(),
                                                          Arg.Any<CancellationToken>())
               .Returns(true);
        sources.TryUpdateDirectoryPublicationAsync(Arg.Any<IDirectoryPublicationLease>(),
                                                    Arg.Any<string?>(),
                                                    Arg.Any<DateTime?>(),
                                                    Arg.Any<string?>(),
                                                    Arg.Any<CancellationToken>())
               .Returns(true);
        modes.HasAnyLibraryDataAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(false);
        modes.GetLibraryDataEvidenceAsync(LibraryId, Arg.Any<CancellationToken>())
             .Returns(new LibraryIngestionDataEvidence(false, false, false, false, false));
        modeLease.LibraryId.Returns(LibraryId);
        modeLease.Mode.Returns(LibraryIngestionMode.Directory);
        modeLease.OwnershipStateAtAcquisition.Returns(existingDefinition == null
                                                           ? LibraryIngestionOwnershipState.Reserved
                                                           : LibraryIngestionOwnershipState.Committed);
        modeLease.OwnershipLostToken.Returns(CancellationToken.None);
        modeLease.TryRenewAsync(Arg.Any<CancellationToken>()).Returns(true);
        modeLease.TryCommitAsync(Arg.Any<CancellationToken>()).Returns(true);
        modeLease.TryAbandonReservationAsync(Arg.Any<CancellationToken>()).Returns(true);
        modeLease.TryDeleteOwnershipAsync(Arg.Any<CancellationToken>()).Returns(true);
        deletion.DeleteVersionUnderModeLeaseAsync(Arg.Any<string?>(),
                                                   LibraryId,
                                                   Arg.Any<string>(),
                                                   modeLease,
                                                   Arg.Any<CancellationToken>())
                .Returns(call =>
                             {
                                 versionRows.Remove(call.ArgAt<string>(2));
                                 return EmptyDeletionResult;
                             });
        deletion.DeleteScanCandidateUnderLeaseAsync(Arg.Any<string?>(),
                                                     LibraryId,
                                                     Arg.Any<string>(),
                                                     Arg.Any<IDirectoryPublicationLease>(),
                                                     modeLease,
                                                     Arg.Any<CancellationToken>())
                .Returns(call =>
                             {
                                 versionRows.Remove(call.ArgAt<string>(2));
                                 return EmptyDeletionResult;
                             });
        deletion.DeleteLibraryUnderModeLeaseAsync(Arg.Any<string?>(),
                                                   LibraryId,
                                                   modeLease,
                                                   Arg.Any<CancellationToken>())
                .Returns(_ =>
                             {
                                 versionRows.Clear();
                                 return EmptyDeletionResult;
                             });
        modeManager.TryAcquireAsync(Arg.Any<string?>(),
                                    LibraryId,
                                    LibraryIngestionMode.Directory,
                                    Arg.Any<CancellationToken>())
                   .Returns(modeLease);
        if (publicationLease != null && existingDefinition != null)
        {
            publicationLease.LibraryId.Returns(LibraryId);
            publicationLease.ScanRunId.Returns("package-import");
            publicationLease.RegistrationRevision.Returns(existingDefinition.RegistrationRevision);
            publicationLease.RegistrationIncarnationId.Returns(existingDefinition.RegistrationIncarnationId);
            publicationLease.OwnershipLostToken.Returns(CancellationToken.None);
            publicationLease.TryRenewAsync(Arg.Any<CancellationToken>()).Returns(true);
            sources.TryAcquireDirectoryPublicationLeaseAsync(LibraryId,
                                                              existingDefinition.RegistrationRevision,
                                                              existingDefinition.RegistrationIncarnationId,
                                                              Arg.Any<string>(),
                                                              existingDefinition.LastPublishedVersion,
                                                              Arg.Any<CancellationToken>())
                   .Returns(publicationLease);
        }

        LibraryImporter importer = includeDocumentRepositories
                                       ? new LibraryImporter(libraries,
                                                             jobs,
                                                             embedding,
                                                             profiles,
                                                             indexes,
                                                             excluded,
                                                             diffs,
                                                             pages,
                                                             chunks,
                                                             bm25,
                                                              sources,
                                                              catalogs,
                                                              assignments,
                                                              compactor,
                                                              databaseResolver,
                                                              deletionService: deletion,
                                                              modeLeaseManager: modeManager,
                                                              modeRepository: modes,
                                                              reembedJobDispatcher: reembedDispatcher)
                                       : new LibraryImporter(libraries,
                                                             jobs,
                                                             embedding,
                                                             profiles,
                                                             indexes,
                                                             excluded,
                                                             diffs,
                                                             pages,
                                                              chunks,
                                                              bm25,
                                                              compactor,
                                                              databaseResolver,
                                                              deletionService: deletion,
                                                              modeLeaseManager: modeManager,
                                                              modeRepository: modes,
                                                              reembedJobDispatcher: reembedDispatcher);
        return new ImportFixture(importer,
                                 libraries,
                                 jobs,
                                 sources,
                                 catalogs,
                                 assignments,
                                 profiles,
                                 indexes,
                                 excluded,
                                 diffs,
                                 pages,
                                 chunks,
                                 bm25,
                                 deletion,
                                 modeManager,
                                 modes,
                                 modeLease,
                                 publicationLease,
                                 reembedDispatcher);
    }

    private ImportFixture WebFixture(bool includeDocumentRepositories = true)
    {
        ImportFixture result = Fixture(existingDefinition: null,
                                       includeDocumentRepositories: includeDocumentRepositories);
        result.Libraries.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
              .Returns(PackagingFixtures.MakeLibrary(LibraryId, Version));
        ConfigureWebMode(result, ownsNewReservation: false);
        return result;
    }

    private ImportFixture ExistingWebFixture(LibraryRecord existingLibrary)
    {
        ImportFixture result = Fixture(existingDefinition: null, existingLibrary);
        ConfigureWebMode(result, ownsNewReservation: false);
        return result;
    }

    private ImportFixture NewWebFixture(bool includeDocumentRepositories = true)
    {
        ImportFixture result = Fixture(existingDefinition: null,
                                       includeDocumentRepositories: includeDocumentRepositories);
        ConfigureWebMode(result, ownsNewReservation: true);
        return result;
    }

    private static void ConfigureWebMode(ImportFixture fixture, bool ownsNewReservation)
    {
        fixture.ModeLease.Mode.Returns(LibraryIngestionMode.Web);
        fixture.ModeLease.OwnershipStateAtAcquisition.Returns(
            ownsNewReservation
                ? LibraryIngestionOwnershipState.Reserved
                : LibraryIngestionOwnershipState.Committed);
        fixture.Modes.GetLibraryDataEvidenceAsync(LibraryId, Arg.Any<CancellationToken>())
               .Returns(new LibraryIngestionDataEvidence(!ownsNewReservation,
                                                         false,
                                                         false,
                                                         !ownsNewReservation,
                                                         false));
        fixture.ModeManager.TryAcquireAsync(Arg.Any<string?>(),
                                            LibraryId,
                                            LibraryIngestionMode.Web,
                                            Arg.Any<CancellationToken>())
               .Returns(fixture.ModeLease);
    }

    private string CreateManifestOnlyBundle(IReadOnlyList<string> versions)
    {
        IReadOnlyList<BundleVersionEntry> entries = versions.Select(version => new BundleVersionEntry
                                                                   {
                                                                       Version = version,
                                                                       EmbeddingProviderId = "onnx-local",
                                                                       EmbeddingModelName = "test-embed",
                                                                       EmbeddingDimensions =
                                                                           PackagingFixtures.DefaultDim,
                                                                       PageCount = 0,
                                                                       ChunkCount = 0,
                                                                       Bm25HasGridFs = false,
                                                                       Blobs =
                                                                           new Dictionary<string, BlobInfo>()
                                                                   })
                                                       .ToList();
        return WriteBundle(new BundleManifest
                               {
                                   ManifestVersion = BundlePaths.CurrentManifestVersion,
                                   ExporterVersion = "test",
                                   CreatedUtc = DateTime.UtcNow,
                                   Library = new BundleLibraryInfo
                                                 {
                                                     Id = LibraryId,
                                                     Name = "Lifecycle test",
                                                     Hint = "Lifecycle test"
                                                 },
                                   Blobs = new Dictionary<string, BlobInfo>(),
                                   Directory = new BundleDirectoryInfo(),
                                   Versions = entries
                               },
                           new Dictionary<string, byte[]>());
    }

    private string CreateInvalidScopedRecordBundle(PackageRecordType recordType,
                                                    InvalidIdentityPart identityPart)
    {
        LibraryRecord library = PackagingFixtures.MakeLibrary(LibraryId, Version);
        SourceDocumentRecord source = new()
                                          {
                                              Id = CreateSourceDocumentId(LibraryId, SourceRelativePath),
                                              LibraryId = LibraryId,
                                              NormalizedRelativePath = SourceRelativePath,
                                              DisplayRelativePath = SourceRelativePath,
                                              DisplayName = "test.pdf",
                                              SourceUri = "saddlerag://library/directory-lifecycle/documents/test",
                                              MediaType = "application/pdf",
                                              FirstSeenVersion = Version,
                                              LastSeenVersion = Version,
                                              CreatedAtUtc = DateTime.UtcNow,
                                              UpdatedAtUtc = DateTime.UtcNow
                                          };
        LibraryVersionRecord version = VersionRecord() with
                                           {
                                               PageCount = 1,
                                               ChunkCount = 1,
                                               PreviousVersion = PreviousVersion
                                           };
        LibraryProfile profile = PackagingFixtures.MakeProfile(LibraryId, Version);
        LibraryIndex index = PackagingFixtures.MakeIndex(LibraryId, Version);
        VersionDiffRecord diff = PackagingFixtures.MakeVersionDiff(LibraryId, PreviousVersion, Version);
        ExcludedSymbol excluded = new()
                                      {
                                          Id = ExcludedSymbol.MakeId(LibraryId, Version, ExcludedName),
                                          LibraryId = LibraryId,
                                          Version = Version,
                                          Name = ExcludedName,
                                          Reason = SymbolRejectionReason.NoStructureSignal,
                                          SampleSentences = [],
                                          ChunkCount = 1,
                                          CapturedUtc = DateTime.UtcNow
                                      };
        PageRecord page = PackagingFixtures.MakePages(LibraryId, Version, count: 1)[0];
        DocChunk chunk = PackagingFixtures.MakeChunks(LibraryId,
                                                       Version,
                                                       count: 1,
                                                       dim: PackagingFixtures.DefaultDim)[0] with
                             {
                                 Embedding = null
                             };

        source = recordType == PackageRecordType.SourceDocument
                     ? CorruptRecord(source, identityPart)
                     : source;
        version = recordType == PackageRecordType.LibraryVersion
                      ? CorruptRecord(version, identityPart)
                      : version;
        profile = recordType == PackageRecordType.Profile
                      ? CorruptRecord(profile, identityPart)
                      : profile;
        index = recordType == PackageRecordType.Index
                    ? CorruptRecord(index, identityPart)
                    : index;
        diff = recordType == PackageRecordType.Diff
                   ? CorruptRecord(diff, identityPart)
                   : diff;
        excluded = recordType == PackageRecordType.ExcludedSymbol
                       ? CorruptRecord(excluded, identityPart)
                       : excluded;
        page = recordType == PackageRecordType.Page
                   ? CorruptRecord(page, identityPart)
                   : page;
        chunk = recordType == PackageRecordType.Chunk
                    ? CorruptRecord(chunk, identityPart)
                    : chunk;

        string versionPath = BundlePaths.VersionFilePath(Version, BundlePaths.VersionFile);
        string profilePath = BundlePaths.VersionFilePath(Version, BundlePaths.ProfileFile);
        string indexPath = BundlePaths.VersionFilePath(Version, BundlePaths.IndexFile);
        string diffPath = BundlePaths.VersionFilePath(Version, BundlePaths.VersionDiffFile);
        string excludedPath = BundlePaths.VersionFilePath(Version, BundlePaths.ExcludedSymbolsFile);
        string pagesPath = BundlePaths.VersionFilePath(Version, BundlePaths.PagesFile);
        string chunksPath = BundlePaths.VersionFilePath(Version, BundlePaths.ChunksFile);
        string embeddingsPath = BundlePaths.VersionFilePath(Version, BundlePaths.EmbeddingsBlobFile);
        var blobs = new Dictionary<string, byte[]>
                        {
                            [BundlePaths.LibraryFile] = Serialize(library),
                            [BundlePaths.SourcesFile] = SerializeJsonl(source),
                            [versionPath] = Serialize(version),
                            [profilePath] = Serialize(profile),
                            [indexPath] = Serialize(index),
                            [diffPath] = Serialize(diff),
                            [excludedPath] = SerializeJsonl(excluded),
                            [pagesPath] = SerializeJsonl(page),
                            [chunksPath] = SerializeJsonl(chunk),
                            [embeddingsPath] = new byte[PackagingFixtures.DefaultDim * sizeof(float)]
                        };
        string versionPrefix = BundlePaths.VersionDir(Version) + "/";
        IReadOnlyDictionary<string, BlobInfo> versionBlobs = blobs
                                                             .Where(blob => blob.Key.StartsWith(versionPrefix,
                                                                                                 StringComparison.Ordinal))
                                                             .ToDictionary(blob => blob.Key,
                                                                           blob => Info(blob.Value),
                                                                           StringComparer.Ordinal);
        var manifest = new BundleManifest
                           {
                               ManifestVersion = BundlePaths.CurrentManifestVersion,
                               ExporterVersion = "test",
                               CreatedUtc = DateTime.UtcNow,
                               Library = new BundleLibraryInfo
                                             {
                                                 Id = LibraryId,
                                                 Name = "Package name",
                                                 Hint = "Package hint"
                                             },
                               Blobs = new Dictionary<string, BlobInfo>
                                           {
                                               [BundlePaths.LibraryFile] = Info(blobs[BundlePaths.LibraryFile]),
                                               [BundlePaths.SourcesFile] = Info(blobs[BundlePaths.SourcesFile])
                                           },
                               Directory = null,
                               Versions =
                               [
                                   new BundleVersionEntry
                                       {
                                           Version = Version,
                                           EmbeddingProviderId = "onnx-local",
                                           EmbeddingModelName = "test-embed",
                                           EmbeddingDimensions = PackagingFixtures.DefaultDim,
                                           PageCount = 1,
                                           ChunkCount = 1,
                                           SourceDocumentCount = 0,
                                           DocumentRevisionCount = 0,
                                           SubjectAssignmentCount = 0,
                                           Bm25HasGridFs = false,
                                           Blobs = versionBlobs
                                       }
                               ]
                           };
        return WriteBundle(manifest, blobs);
    }

    private string CreateBm25Bundle(Bm25Corruption corruption)
    {
        LibraryRecord library = PackagingFixtures.MakeLibrary(LibraryId, Version);
        LibraryVersionRecord version = VersionRecord() with { PageCount = 1, ChunkCount = 1 };
        PageRecord page = PackagingFixtures.MakePages(LibraryId, Version, count: 1)[0];
        DocChunk chunk = PackagingFixtures.MakeChunks(LibraryId,
                                                       Version,
                                                       count: 1,
                                                       dim: PackagingFixtures.DefaultDim)[0] with
                             {
                                 Embedding = null
                             };
        int shardCount = corruption == Bm25Corruption.WrongShardRouting ? 64 : 1;
        Bm25BuildResult build = Bm25IndexBuilder.Build(LibraryId, Version, [chunk], shardCount);
        LibraryIndex index = PackagingFixtures.MakeIndex(LibraryId, Version) with { Bm25 = build.Stats };
        Bm25PackageData bm25 = CorruptBm25Package(corruption, index, build.Shards);

        string versionPath = BundlePaths.VersionFilePath(Version, BundlePaths.VersionFile);
        string indexPath = BundlePaths.VersionFilePath(Version, BundlePaths.IndexFile);
        string pagesPath = BundlePaths.VersionFilePath(Version, BundlePaths.PagesFile);
        string chunksPath = BundlePaths.VersionFilePath(Version, BundlePaths.ChunksFile);
        string embeddingsPath = BundlePaths.VersionFilePath(Version, BundlePaths.EmbeddingsBlobFile);
        string shardsPath = BundlePaths.VersionFilePath(Version, BundlePaths.Bm25ShardsFile);
        var blobs = new Dictionary<string, byte[]>
                        {
                            [BundlePaths.LibraryFile] = Serialize(library),
                            [versionPath] = Serialize(version),
                            [indexPath] = Serialize(bm25.Index),
                            [pagesPath] = SerializeJsonl(page),
                            [chunksPath] = SerializeJsonl(chunk),
                            [embeddingsPath] = new byte[PackagingFixtures.DefaultDim * sizeof(float)],
                            [shardsPath] = SerializeJsonlRows(bm25.Shards)
                        };
        foreach((string gridFsId, byte[] payload) in bm25.GridFsPayloads)
            blobs[BundlePaths.Bm25GridFsBlob(Version, gridFsId)] = payload;

        string versionPrefix = BundlePaths.VersionDir(Version) + "/";
        IReadOnlyDictionary<string, BlobInfo> versionBlobs = blobs
                                                             .Where(blob => blob.Key.StartsWith(versionPrefix,
                                                                                                 StringComparison.Ordinal))
                                                             .ToDictionary(blob => blob.Key,
                                                                           blob => Info(blob.Value),
                                                                           StringComparer.Ordinal);
        var manifest = new BundleManifest
                           {
                               ManifestVersion = BundlePaths.CurrentManifestVersion,
                               ExporterVersion = "test",
                               CreatedUtc = DateTime.UtcNow,
                               Library = new BundleLibraryInfo
                                             {
                                                 Id = LibraryId,
                                                 Name = "Package name",
                                                 Hint = "Package hint"
                                             },
                               Blobs = new Dictionary<string, BlobInfo>
                                           {
                                               [BundlePaths.LibraryFile] = Info(blobs[BundlePaths.LibraryFile])
                                           },
                               Directory = null,
                               Versions =
                               [
                                   new BundleVersionEntry
                                       {
                                           Version = Version,
                                           EmbeddingProviderId = "onnx-local",
                                           EmbeddingModelName = "test-embed",
                                           EmbeddingDimensions = PackagingFixtures.DefaultDim,
                                           PageCount = 1,
                                           ChunkCount = 1,
                                           SourceDocumentCount = 0,
                                           DocumentRevisionCount = 0,
                                           SubjectAssignmentCount = 0,
                                           Bm25HasGridFs = bm25.GridFsPayloads.Count > 0,
                                           Blobs = versionBlobs
                                       }
                               ]
                           };
        return WriteBundle(manifest, blobs);
    }

    private static Bm25PackageData CorruptBm25Package(Bm25Corruption corruption,
                                                       LibraryIndex index,
                                                       IReadOnlyList<Bm25Shard> builtShards)
    {
        var shards = builtShards.ToList();
        var gridFsPayloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        Bm25Shard shard = shards[0];
        string term = shard.InlineTerms.Keys.OrderBy(value => value, StringComparer.Ordinal).First();
        IReadOnlyList<Bm25Posting> postings = shard.InlineTerms[term];
        IReadOnlyList<Bm25Posting> foreignPostings = postings
                                                     .Select(posting => posting with
                                                                            {
                                                                                ChunkId = ForeignRecordId
                                                                            })
                                                     .ToList();

        switch(corruption)
        {
            case Bm25Corruption.ValidExternalPayload:
            {
                var inline = new Dictionary<string, IReadOnlyList<Bm25Posting>>(shard.InlineTerms,
                                                                                StringComparer.Ordinal);
                var external = new Dictionary<string, string>(StringComparer.Ordinal);
                var payloadIndex = 0;
                foreach((string externalTerm, IReadOnlyList<Bm25Posting> externalPostings) in
                        shard.InlineTerms)
                {
                    string gridFsId = $"valid-external-{payloadIndex++}";
                    inline.Remove(externalTerm);
                    external[externalTerm] = gridFsId;
                    gridFsPayloads[gridFsId] = Bm25ShardRepository.SerializePostings(externalPostings);
                }
                shards[0] = shard with { InlineTerms = inline, ExternalTerms = external };
                break;
            }
            case Bm25Corruption.InlineForeignChunk:
            {
                var inline = new Dictionary<string, IReadOnlyList<Bm25Posting>>(shard.InlineTerms,
                                                                                StringComparer.Ordinal)
                                 {
                                     [term] = foreignPostings
                                 };
                shards[0] = shard with { InlineTerms = inline };
                break;
            }
            case Bm25Corruption.ExternalForeignChunk:
            case Bm25Corruption.ExternalMalformedPayload:
            {
                const string GridFsId = "external-term-payload";
                var inline = new Dictionary<string, IReadOnlyList<Bm25Posting>>(shard.InlineTerms,
                                                                                StringComparer.Ordinal);
                inline.Remove(term);
                shards[0] = shard with
                                {
                                    InlineTerms = inline,
                                    ExternalTerms = new Dictionary<string, string>(StringComparer.Ordinal)
                                                        {
                                                            [term] = GridFsId
                                                        }
                                };
                gridFsPayloads[GridFsId] = corruption == Bm25Corruption.ExternalMalformedPayload
                                               ? [1, 2, 3, 4]
                                               : Bm25ShardRepository.SerializePostings(foreignPostings);
                break;
            }
            case Bm25Corruption.WholeShardForeignChunk:
            {
                const string GridFsId = "whole-shard-payload";
                var payloadTerms = new Dictionary<string, IReadOnlyList<Bm25Posting>>(shard.InlineTerms,
                                                                                       StringComparer.Ordinal)
                                       {
                                           [term] = foreignPostings
                                       };
                shards[0] = shard with
                                {
                                    InlineTerms = new Dictionary<string, IReadOnlyList<Bm25Posting>>(),
                                    ExternalTerms = new Dictionary<string, string>(),
                                    ShardGridFsRef = GridFsId
                                };
                gridFsPayloads[GridFsId] = Bm25ShardRepository.SerializePostingsDictionary(payloadTerms);
                break;
            }
            case Bm25Corruption.WrongShardRouting:
                MoveTermToWrongShard(shards, shard, term, postings, index.Bm25.ShardCount);
                break;
            case Bm25Corruption.DocumentCountMismatch:
                index = index with
                            {
                                Bm25 = index.Bm25 with { DocumentCount = index.Bm25.DocumentCount + 1 }
                            };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, message: null);
        }

        return new Bm25PackageData(index, shards, gridFsPayloads);
    }

    private static void MoveTermToWrongShard(IList<Bm25Shard> shards,
                                             Bm25Shard sourceShard,
                                             string term,
                                             IReadOnlyList<Bm25Posting> postings,
                                             int shardCount)
    {
        var sourceTerms = new Dictionary<string, IReadOnlyList<Bm25Posting>>(sourceShard.InlineTerms,
                                                                             StringComparer.Ordinal);
        sourceTerms.Remove(term);
        int sourcePosition = shards.IndexOf(sourceShard);
        if (sourceTerms.Count == 0)
            shards.RemoveAt(sourcePosition);
        else
            shards[sourcePosition] = sourceShard with { InlineTerms = sourceTerms };

        int wrongIndex = (sourceShard.ShardIndex + 1) % shardCount;
        int targetPosition = shards.ToList().FindIndex(candidate => candidate.ShardIndex == wrongIndex);
        if (targetPosition >= 0)
        {
            Bm25Shard target = shards[targetPosition];
            var targetTerms = new Dictionary<string, IReadOnlyList<Bm25Posting>>(target.InlineTerms,
                                                                                 StringComparer.Ordinal)
                                  {
                                      [term] = postings
                                  };
            shards[targetPosition] = target with { InlineTerms = targetTerms };
        }
        else
        {
            shards.Add(new Bm25Shard
                           {
                               Id = $"{LibraryId}/{Version}/{wrongIndex}",
                               LibraryId = LibraryId,
                               Version = Version,
                               ShardIndex = wrongIndex,
                               InlineTerms = new Dictionary<string, IReadOnlyList<Bm25Posting>>(
                                   StringComparer.Ordinal)
                                                 {
                                                     [term] = postings
                                                 }
                           });
        }
    }

    private static byte[] SerializeJsonlRows<T>(IEnumerable<T> values)
    {
        string result = string.Concat(values.Select(value =>
            JsonSerializer.Serialize(value, BundleJsonOptions.JsonlDefault) + "\n"));
        return Encoding.UTF8.GetBytes(result);
    }

    private static SourceDocumentRecord CorruptRecord(SourceDocumentRecord record,
                                                       InvalidIdentityPart identityPart) =>
        identityPart switch
        {
            InvalidIdentityPart.Library => record with
                                               {
                                                   Id = CreateSourceDocumentId(ForeignLibraryId,
                                                                               record.NormalizedRelativePath),
                                                   LibraryId = ForeignLibraryId
                                               },
            InvalidIdentityPart.Id => record with { Id = ForeignRecordId },
            _ => throw new ArgumentOutOfRangeException(nameof(identityPart), identityPart, message: null)
        };

    private static LibraryVersionRecord CorruptRecord(LibraryVersionRecord record,
                                                       InvalidIdentityPart identityPart) =>
        identityPart switch
        {
            InvalidIdentityPart.Library => record with
                                               {
                                                   Id = $"{ForeignLibraryId}/{record.Version}",
                                                   LibraryId = ForeignLibraryId
                                               },
            InvalidIdentityPart.Version => record with
                                               {
                                                   Id = $"{record.LibraryId}/{ForeignVersion}",
                                                   Version = ForeignVersion
                                               },
            InvalidIdentityPart.Id => record with { Id = InvalidRecordId },
            _ => throw new ArgumentOutOfRangeException(nameof(identityPart), identityPart, message: null)
        };

    private static LibraryProfile CorruptRecord(LibraryProfile record, InvalidIdentityPart identityPart) =>
        identityPart switch
        {
            InvalidIdentityPart.Library => record with
                                               {
                                                   Id = $"{ForeignLibraryId}/{record.Version}",
                                                   LibraryId = ForeignLibraryId
                                               },
            InvalidIdentityPart.Version => record with
                                               {
                                                   Id = $"{record.LibraryId}/{ForeignVersion}",
                                                   Version = ForeignVersion
                                               },
            InvalidIdentityPart.Id => record with { Id = InvalidRecordId },
            _ => throw new ArgumentOutOfRangeException(nameof(identityPart), identityPart, message: null)
        };

    private static LibraryIndex CorruptRecord(LibraryIndex record, InvalidIdentityPart identityPart) =>
        identityPart switch
        {
            InvalidIdentityPart.Library => record with
                                               {
                                                   Id = $"{ForeignLibraryId}/{record.Version}",
                                                   LibraryId = ForeignLibraryId
                                               },
            InvalidIdentityPart.Version => record with
                                               {
                                                   Id = $"{record.LibraryId}/{ForeignVersion}",
                                                   Version = ForeignVersion
                                               },
            InvalidIdentityPart.Id => record with { Id = InvalidRecordId },
            _ => throw new ArgumentOutOfRangeException(nameof(identityPart), identityPart, message: null)
        };

    private static VersionDiffRecord CorruptRecord(VersionDiffRecord record, InvalidIdentityPart identityPart) =>
        identityPart switch
        {
            InvalidIdentityPart.Library => record with
                                               {
                                                   Id = $"{ForeignLibraryId}/{record.FromVersion}-to-{record.ToVersion}",
                                                   LibraryId = ForeignLibraryId
                                               },
            InvalidIdentityPart.Version => record with
                                               {
                                                   Id = $"{record.LibraryId}/{record.FromVersion}-to-{ForeignVersion}",
                                                   ToVersion = ForeignVersion
                                               },
            InvalidIdentityPart.Id => record with { Id = InvalidRecordId },
            _ => throw new ArgumentOutOfRangeException(nameof(identityPart), identityPart, message: null)
        };

    private static ExcludedSymbol CorruptRecord(ExcludedSymbol record, InvalidIdentityPart identityPart) =>
        identityPart switch
        {
            InvalidIdentityPart.Library => record with
                                               {
                                                   Id = ExcludedSymbol.MakeId(ForeignLibraryId,
                                                                              record.Version,
                                                                              record.Name),
                                                   LibraryId = ForeignLibraryId
                                               },
            InvalidIdentityPart.Version => record with
                                               {
                                                   Id = ExcludedSymbol.MakeId(record.LibraryId,
                                                                              ForeignVersion,
                                                                              record.Name),
                                                   Version = ForeignVersion
                                               },
            InvalidIdentityPart.Id => record with { Id = InvalidRecordId },
            _ => throw new ArgumentOutOfRangeException(nameof(identityPart), identityPart, message: null)
        };

    private static PageRecord CorruptRecord(PageRecord record, InvalidIdentityPart identityPart) =>
        identityPart switch
        {
            InvalidIdentityPart.Library => record with { LibraryId = ForeignLibraryId },
            InvalidIdentityPart.Version => record with { Version = ForeignVersion },
            InvalidIdentityPart.Id => record with { Id = ForeignRecordId },
            _ => throw new ArgumentOutOfRangeException(nameof(identityPart), identityPart, message: null)
        };

    private static DocChunk CorruptRecord(DocChunk record, InvalidIdentityPart identityPart) =>
        identityPart switch
        {
            InvalidIdentityPart.Library => record with { LibraryId = ForeignLibraryId },
            InvalidIdentityPart.Version => record with { Version = ForeignVersion },
            InvalidIdentityPart.Id => record with { Id = ForeignRecordId },
            _ => throw new ArgumentOutOfRangeException(nameof(identityPart), identityPart, message: null)
        };

    private static async Task AssertNoPurgeOrWritesAsync(ImportFixture fixture)
    {
        await fixture.ModeManager.DidNotReceive()
                     .TryAcquireAsync(Arg.Any<string?>(),
                                      Arg.Any<string>(),
                                      Arg.Any<LibraryIngestionMode>(),
                                      Arg.Any<CancellationToken>());
        await fixture.Deletion.DidNotReceive()
                     .DeleteVersionUnderModeLeaseAsync(Arg.Any<string?>(),
                                                       Arg.Any<string>(),
                                                       Arg.Any<string>(),
                                                       Arg.Any<ILibraryIngestionModeLease>(),
                                                       Arg.Any<CancellationToken>());
        await fixture.Libraries.DidNotReceive()
                     .UpsertVersionAsync(Arg.Any<LibraryVersionRecord>(), Arg.Any<CancellationToken>());
        await fixture.Libraries.DidNotReceive()
                     .UpsertLibraryAsync(Arg.Any<LibraryRecord>(), Arg.Any<CancellationToken>());
        await fixture.Profiles.DidNotReceive()
                     .UpsertAsync(Arg.Any<LibraryProfile>(), Arg.Any<CancellationToken>());
        await fixture.Indexes.DidNotReceive()
                     .UpsertAsync(Arg.Any<LibraryIndex>(), Arg.Any<CancellationToken>());
        await fixture.Diffs.DidNotReceive()
                     .UpsertDiffAsync(Arg.Any<VersionDiffRecord>(), Arg.Any<CancellationToken>());
        await fixture.Excluded.DidNotReceive()
                     .UpsertManyAsync(Arg.Any<IEnumerable<ExcludedSymbol>>(), Arg.Any<CancellationToken>());
        await fixture.Pages.DidNotReceive()
                     .UpsertPageAsync(Arg.Any<PageRecord>(), Arg.Any<CancellationToken>());
        await fixture.Chunks.DidNotReceive()
                     .InsertChunksAsync(Arg.Any<IReadOnlyList<DocChunk>>(), Arg.Any<CancellationToken>());
        await fixture.Bm25.DidNotReceive()
                     .UploadGridFsBlobAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await fixture.Bm25.DidNotReceive()
                     .UploadGridFsBlobAsync(Arg.Any<string>(),
                                            Arg.Any<Stream>(),
                                            Arg.Any<CancellationToken>());
        await fixture.Bm25.DidNotReceive()
                     .UpsertShardAsync(Arg.Any<Bm25Shard>(), Arg.Any<CancellationToken>());
        await fixture.Sources.DidNotReceive()
                     .GetOrCreateDocumentAsync(Arg.Any<SourceDocumentRecord>(), Arg.Any<CancellationToken>());
        await fixture.Sources.DidNotReceive()
                     .PersistRevisionAsync(Arg.Any<DocumentRevisionRecord>(),
                                           Arg.Any<Stream>(),
                                           Arg.Any<Stream?>(),
                                           Arg.Any<CancellationToken>());
        await fixture.Catalogs.DidNotReceive()
                     .InsertRevisionAsync(Arg.Any<SubjectCatalogRecord>(), Arg.Any<CancellationToken>());
        await fixture.Assignments.DidNotReceive()
                     .PersistAsync(Arg.Any<SubjectAssignmentRecord>(), Arg.Any<CancellationToken>());
        await fixture.Jobs.DidNotReceive()
                     .UpsertAsync(Arg.Any<JobRecord>(), Arg.Any<CancellationToken>());
    }

    private static byte[] Serialize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, BundleJsonOptions.Default);

    private static byte[] SerializeJsonl<T>(T value) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, BundleJsonOptions.JsonlDefault) + "\n");

    private static byte[] SerializeJsonlIncludingNulls<T>(T value)
    {
        var options = new JsonSerializerOptions(BundleJsonOptions.JsonlDefault)
                          {
                              DefaultIgnoreCondition =
                                  System.Text.Json.Serialization.JsonIgnoreCondition.Never
                          };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, options) + "\n");
    }

    private static string CreateSourceDocumentId(string libraryId, string normalizedRelativePath)
    {
        string identity = string.Join('\u001f', libraryId, normalizedRelativePath);
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return $"source-document-{hash}";
    }

    private string CreateValidBundle(IReadOnlyList<string> versions)
    {
        LibraryRecord library = PackagingFixtures.MakeLibrary(LibraryId,
                                                               Version,
                                                               versions.ToArray());
        byte[] libraryBytes = JsonSerializer.SerializeToUtf8Bytes(library, BundleJsonOptions.Default);
        var blobs = new Dictionary<string, byte[]>
                        {
                            [BundlePaths.LibraryFile] = libraryBytes
                        };
        var manifestVersions = new List<BundleVersionEntry>();
        foreach(string versionName in versions)
        {
            LibraryVersionRecord version = string.Equals(versionName, Version, StringComparison.Ordinal)
                                               ? VersionRecord()
                                               : PackagingFixtures.MakeVersion(
                                                   LibraryId,
                                                   versionName,
                                                   pageCount: 0,
                                                   chunkCount: 0,
                                                   dim: PackagingFixtures.DefaultDim);
            byte[] versionBytes = JsonSerializer.SerializeToUtf8Bytes(version, BundleJsonOptions.Default);
            string versionPath = BundlePaths.VersionFilePath(versionName, BundlePaths.VersionFile);
            blobs[versionPath] = versionBytes;
            manifestVersions.Add(new BundleVersionEntry
                                     {
                                         Version = versionName,
                                         EmbeddingProviderId = version.EmbeddingProviderId,
                                         EmbeddingModelName = version.EmbeddingModelName,
                                         EmbeddingDimensions = version.EmbeddingDimensions,
                                         PageCount = 0,
                                         ChunkCount = 0,
                                         Bm25HasGridFs = false,
                                         Blobs = new Dictionary<string, BlobInfo>
                                                     {
                                                         [versionPath] = Info(versionBytes)
                                                     }
                                     });
        }

        var manifest = new BundleManifest
                           {
                               ManifestVersion = BundlePaths.CurrentManifestVersion,
                               ExporterVersion = "test",
                               CreatedUtc = DateTime.UtcNow,
                               Library = new BundleLibraryInfo
                                             {
                                                 Id = LibraryId,
                                                 Name = "Package name",
                                                 Hint = "Package hint"
                                             },
                               Blobs = new Dictionary<string, BlobInfo>
                                           {
                                               [BundlePaths.LibraryFile] = Info(libraryBytes)
                                           },
                               Directory = new BundleDirectoryInfo
                                               {
                                                   Recursive = true,
                                                   AllowedExtensions = [".pdf", ".docx"],
                                                   ExclusionPatterns = ["**/archive/**"]
                                               },
                               Versions = manifestVersions
                           };
        return WriteBundle(manifest, blobs);
    }

    private string CreateValidBundle(bool isDirectory = true,
                                     bool encoderMatches = true,
                                     bool nullExtensions = false,
                                     bool nullExclusions = false)
    {
        LibraryRecord library = PackagingFixtures.MakeLibrary(LibraryId, Version);
        LibraryVersionRecord version = encoderMatches
                                           ? VersionRecord()
                                           : VersionRecord() with
                                               {
                                                   EmbeddingProviderId = "bundle-embedding-provider"
                                               };
        byte[] libraryBytes = JsonSerializer.SerializeToUtf8Bytes(library, BundleJsonOptions.Default);
        byte[] versionBytes = JsonSerializer.SerializeToUtf8Bytes(version, BundleJsonOptions.Default);
        string versionPath = BundlePaths.VersionFilePath(Version, BundlePaths.VersionFile);
        var blobs = new Dictionary<string, byte[]>
                        {
                            [BundlePaths.LibraryFile] = libraryBytes,
                            [versionPath] = versionBytes
                        };
        var manifest = new BundleManifest
                           {
                               ManifestVersion = BundlePaths.CurrentManifestVersion,
                               ExporterVersion = "test",
                               CreatedUtc = DateTime.UtcNow,
                               Library = new BundleLibraryInfo
                                             {
                                                 Id = LibraryId,
                                                 Name = "Package name",
                                                 Hint = "Package hint"
                                             },
                               Blobs = new Dictionary<string, BlobInfo>
                                           {
                                               [BundlePaths.LibraryFile] = Info(libraryBytes)
                                           },
                               Directory = isDirectory
                                               ? new BundleDirectoryInfo
                                                     {
                                                         Recursive = true,
                                                         AllowedExtensions = nullExtensions
                                                                                 ? null!
                                                                                 : [".pdf", ".docx"],
                                                         ExclusionPatterns = nullExclusions
                                                                                 ? null!
                                                                                 : ["**/archive/**"]
                                                     }
                                               : null,
                               Versions =
                               [
                                   new BundleVersionEntry
                                       {
                                           Version = Version,
                                            EmbeddingProviderId = version.EmbeddingProviderId,
                                            EmbeddingModelName = version.EmbeddingModelName,
                                            EmbeddingDimensions = version.EmbeddingDimensions,
                                           PageCount = 0,
                                           ChunkCount = 0,
                                           Bm25HasGridFs = false,
                                           Blobs = new Dictionary<string, BlobInfo>
                                                       {
                                                           [versionPath] = Info(versionBytes)
                                                       }
                                       }
                               ]
                           };
        return WriteBundle(manifest,
                           blobs,
                           includeNullManifestValues: nullExtensions || nullExclusions);
    }

    private string CreateSubjectBundle(bool invalidPrimary = false,
                                       bool invalidSecondary = false,
                                       bool includeAssignment = true,
                                       string? versionTaxonomy = SubjectTaxonomyVersion,
                                       string assignmentTaxonomy = SubjectTaxonomyVersion,
                                       bool includeCatalog = true,
                                       InvalidSubjectCatalogShape? invalidCatalogShape = null)
    {
        byte[] original = [0x25, 0x50, 0x44, 0x46, 0x0A];
        string artifactHash = Convert.ToHexStringLower(SHA256.HashData(original));
        string artifactPath = BundlePaths.DocumentArtifact(artifactHash);
        string documentId = CreateSourceDocumentId(LibraryId, SourceRelativePath);
        string revisionId = SourceDocumentRepository.MakeRevisionId(LibraryId, Version, documentId);
        var source = new SourceDocumentRecord
                         {
                             Id = documentId,
                             LibraryId = LibraryId,
                             NormalizedRelativePath = SourceRelativePath,
                             DisplayRelativePath = SourceRelativePath,
                             DisplayName = "test.pdf",
                             SourceUri = $"saddlerag://library/{LibraryId}/documents/{documentId}",
                             MediaType = "application/pdf",
                             FirstSeenVersion = Version,
                             LastSeenVersion = Version,
                             CreatedAtUtc = SubjectRecordedAtUtc,
                             UpdatedAtUtc = SubjectRecordedAtUtc
                         };
        var revision = new DocumentRevisionRecord
                           {
                               Id = revisionId,
                               DocumentId = documentId,
                               LibraryId = LibraryId,
                               Version = Version,
                               ScanRunId = SubjectScanRunId,
                               State = DocumentRevisionState.Published,
                               AcquiredAtUtc = SubjectRecordedAtUtc,
                               OriginalArtifactHash = artifactHash,
                               OriginalByteLength = original.LongLength,
                               OriginalMediaType = "application/pdf",
                               PublishedAtUtc = SubjectRecordedAtUtc
                           };
        SubjectCatalogRecord catalog = ApplyInvalidSubjectCatalogShape(SubjectCatalog(), invalidCatalogShape);
        var assignment = new SubjectAssignmentRecord
                             {
                                 Id = SubjectAssignmentRepository.MakeId(LibraryId, Version, revisionId),
                                 LibraryId = LibraryId,
                                 Version = Version,
                                 ScanRunId = SubjectScanRunId,
                                 DocumentId = documentId,
                                 DocumentRevisionId = revisionId,
                                 TaxonomyVersion = assignmentTaxonomy,
                                 Primary = new SubjectSelection
                                               {
                                                   SubjectId = invalidPrimary
                                                                   ? DanglingSubjectId
                                                                   : PrimarySubjectId,
                                                   Confidence = 0.95f,
                                                   Evidence = ["primary evidence"]
                                               },
                                 Secondary =
                                 [
                                     new SubjectSelection
                                         {
                                             SubjectId = invalidSecondary
                                                             ? DanglingSubjectId
                                                             : SecondarySubjectId,
                                             Confidence = 0.75f,
                                             Evidence = ["secondary evidence"]
                                         }
                                 ],
                                 NeedsReview = false,
                                 Provenance = SubjectProvenance()
                             };
        LibraryRecord library = PackagingFixtures.MakeLibrary(LibraryId, Version);
        LibraryVersionRecord version = VersionRecord() with
                                           {
                                               SubjectTaxonomyVersion = versionTaxonomy,
                                               ScanRunId = SubjectScanRunId
                                           };
        string versionPath = BundlePaths.VersionFilePath(Version, BundlePaths.VersionFile);
        string revisionsPath = BundlePaths.VersionFilePath(Version, BundlePaths.DocumentRevisionsFile);
        string assignmentsPath = BundlePaths.VersionFilePath(Version, BundlePaths.SubjectAssignmentsFile);
        var blobs = new Dictionary<string, byte[]>
                        {
                            [BundlePaths.LibraryFile] = Serialize(library),
                            [BundlePaths.SourcesFile] = SerializeJsonl(source),
                            [artifactPath] = original,
                            [versionPath] = Serialize(version),
                            [revisionsPath] = SerializeJsonl(revision)
                        };
        if (includeCatalog)
        {
            bool includeNulls = invalidCatalogShape is InvalidSubjectCatalogShape.NullConceptList or
                                InvalidSubjectCatalogShape.NullAliasList;
            blobs[BundlePaths.SubjectCatalogsFile] = includeNulls
                                                         ? SerializeJsonlIncludingNulls(catalog)
                                                         : SerializeJsonl(catalog);
        }
        if (includeAssignment)
            blobs[assignmentsPath] = SerializeJsonl(assignment);
        var topLevelBlobs = new Dictionary<string, BlobInfo>
                                {
                                    [BundlePaths.LibraryFile] = Info(blobs[BundlePaths.LibraryFile]),
                                    [BundlePaths.SourcesFile] = Info(blobs[BundlePaths.SourcesFile]),
                                    [artifactPath] = Info(original)
                                };
        if (includeCatalog)
            topLevelBlobs[BundlePaths.SubjectCatalogsFile] = Info(blobs[BundlePaths.SubjectCatalogsFile]);
        var versionBlobs = new Dictionary<string, BlobInfo>
                               {
                                   [versionPath] = Info(blobs[versionPath]),
                                   [revisionsPath] = Info(blobs[revisionsPath])
                               };
        if (includeAssignment)
            versionBlobs[assignmentsPath] = Info(blobs[assignmentsPath]);
        var manifest = new BundleManifest
                           {
                               ManifestVersion = BundlePaths.CurrentManifestVersion,
                               ExporterVersion = "test",
                               CreatedUtc = SubjectRecordedAtUtc,
                               Library = new BundleLibraryInfo
                                             {
                                                 Id = LibraryId,
                                                 Name = library.Name,
                                                 Hint = library.Hint
                                             },
                               Blobs = topLevelBlobs,
                               Directory = new BundleDirectoryInfo
                                               {
                                                   Recursive = true,
                                                   AllowedExtensions = [".pdf"]
                                               },
                               Versions =
                               [
                                   new BundleVersionEntry
                                       {
                                           Version = Version,
                                           EmbeddingProviderId = version.EmbeddingProviderId,
                                           EmbeddingModelName = version.EmbeddingModelName,
                                           EmbeddingDimensions = version.EmbeddingDimensions,
                                           PageCount = 0,
                                           ChunkCount = 0,
                                           SourceDocumentCount = 1,
                                           DocumentRevisionCount = 1,
                                           SubjectAssignmentCount = includeAssignment ? 1 : 0,
                                           Bm25HasGridFs = false,
                                           Blobs = versionBlobs
                                       }
                               ]
                           };
        return WriteBundle(manifest, blobs);
    }

    private static SubjectCatalogRecord ApplyInvalidSubjectCatalogShape(
        SubjectCatalogRecord catalog,
        InvalidSubjectCatalogShape? invalidShape)
    {
        SubjectConcept first = catalog.Concepts[0];
        SubjectCatalogRecord result = invalidShape switch
            {
                InvalidSubjectCatalogShape.EmptyCatalog => catalog with { Concepts = [] },
                InvalidSubjectCatalogShape.NullConceptList => catalog with { Concepts = null! },
                InvalidSubjectCatalogShape.NullConcept => catalog with { Concepts = [null!] },
                InvalidSubjectCatalogShape.MissingLabel => ReplaceFirstConcept(
                    catalog,
                    first with { Label = " " }),
                InvalidSubjectCatalogShape.MissingDescription => ReplaceFirstConcept(
                    catalog,
                    first with { Description = " " }),
                InvalidSubjectCatalogShape.NullAliasList => ReplaceFirstConcept(
                    catalog,
                    first with { Aliases = null! }),
                InvalidSubjectCatalogShape.BlankAlias => ReplaceFirstConcept(
                    catalog,
                    first with { Aliases = [" "] }),
                InvalidSubjectCatalogShape.DuplicateAlias => ReplaceFirstConcept(
                    catalog,
                    first with { Aliases = ["fluid power", "FLUID POWER"] }),
                InvalidSubjectCatalogShape.CandidateCatalog => catalog with
                                                                    {
                                                                        PublicationState =
                                                                            SubjectCatalogPublicationState.Candidate
                                                                    },
                _ => catalog
            };
        return result;
    }

    private static SubjectCatalogRecord ReplaceFirstConcept(SubjectCatalogRecord catalog,
                                                             SubjectConcept first) =>
        catalog with { Concepts = [first, .. catalog.Concepts.Skip(1)] };

    private static SubjectCatalogRecord SubjectCatalog() => new()
        {
            Id = SubjectCatalogRepository.MakeId(LibraryId, SubjectTaxonomyVersion),
            LibraryId = LibraryId,
            Revision = 1,
            TaxonomyVersion = SubjectTaxonomyVersion,
            ScanRunId = SubjectScanRunId,
            Concepts =
            [
                new SubjectConcept
                    {
                        Id = PrimarySubjectId,
                        Label = "Hydraulics",
                        Description = "Hydraulic service"
                    },
                new SubjectConcept
                    {
                        Id = SecondarySubjectId,
                        Label = "Safety",
                        Description = "Safe service"
                    }
            ],
            Provenance = SubjectProvenance(),
            CreatedAtUtc = SubjectRecordedAtUtc
        };

    private static SubjectClassifierProvenance SubjectProvenance() => new()
        {
            Backend = "onnx",
            ModelId = "subject-test-model",
            PromptVersion = "subject-v1",
            GeneratedAtUtc = SubjectRecordedAtUtc
        };

    private string WriteBundle(BundleManifest manifest,
                               IReadOnlyDictionary<string, byte[]> blobs,
                               bool includeNullManifestValues = false)
    {
        string path = Path.Combine(Path.GetTempPath(), $"directory-lifecycle-{Guid.NewGuid():N}.srlib.zip");
        mBundlePaths.Add(path);
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach((string name, byte[] bytes) in blobs)
        {
            using Stream stream = archive.CreateEntry(name).Open();
            stream.Write(bytes);
        }
        using(Stream stream = archive.CreateEntry(BundlePaths.ManifestFile).Open())
        {
            JsonSerializerOptions options = includeNullManifestValues
                                                ? new JsonSerializerOptions(BundleJsonOptions.Default)
                                                      {
                                                          DefaultIgnoreCondition =
                                                              System.Text.Json.Serialization.JsonIgnoreCondition.Never
                                                      }
                                                : BundleJsonOptions.Default;
            JsonSerializer.Serialize(stream, manifest, options);
        }
        return path;
    }

    private static BlobInfo Info(byte[] bytes) => new()
        {
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Bytes = bytes.LongLength
        };

    private static DirectoryLibraryDefinition ExistingDefinition(string version = Version) => new()
        {
            Id = LibraryId,
            RootPath = "C:\\LocalDocs",
            BindingStatus = DirectoryLibraryBindingStatus.Bound,
            RegisteredAtUtc = DateTime.UtcNow,
            RegistrationRevision = 7,
            RegistrationIncarnationId = "local-registration",
            LastPublishedAtUtc = VersionRecord().ScrapedAt,
            LastPublishedVersion = version
        };

    private static LibraryRecord PreviousLibrary() => new()
        {
            Id = LibraryId,
            Name = "Local library name",
            Hint = "Local library hint",
            CurrentVersion = PreviousVersion,
            AllVersions = [PreviousVersion]
        };

    private static bool LibraryMatches(LibraryRecord? actual, LibraryRecord expected) =>
        actual != null &&
        string.Equals(actual.Id, expected.Id, StringComparison.Ordinal) &&
        string.Equals(actual.Name, expected.Name, StringComparison.Ordinal) &&
        string.Equals(actual.Hint, expected.Hint, StringComparison.Ordinal) &&
        string.Equals(actual.CurrentVersion, expected.CurrentVersion, StringComparison.Ordinal) &&
        actual.AllVersions.SequenceEqual(expected.AllVersions, StringComparer.Ordinal);

    private static JobRecord MongoRoundTrip(JobRecord source)
    {
        DateTime utcCreatedAt = source.CreatedAt.ToUniversalTime();
        long normalizedTicks = utcCreatedAt.Ticks - utcCreatedAt.Ticks % TimeSpan.TicksPerMillisecond;
        return new JobRecord
                   {
                       Id = source.Id,
                       JobType = source.JobType,
                       Profile = source.Profile,
                       LibraryId = source.LibraryId,
                       Version = source.Version,
                       InputJson = source.InputJson,
                       Status = source.Status,
                       PipelineState = source.PipelineState,
                       CreatedAt = new DateTime(normalizedTicks, DateTimeKind.Utc),
                       ItemsLabel = source.ItemsLabel
                   };
    }

    private static LibraryVersionRecord VersionRecord() =>
        PackagingFixtures.MakeVersion(LibraryId,
                                      Version,
                                      pageCount: 0,
                                      chunkCount: 0,
                                      dim: PackagingFixtures.DefaultDim);

    private sealed record ImportFixture(LibraryImporter Importer,
                                         ILibraryRepository Libraries,
                                         IJobRepository Jobs,
                                         ISourceDocumentRepository Sources,
                                         ISubjectCatalogRepository Catalogs,
                                         ISubjectAssignmentRepository Assignments,
                                         ILibraryProfileRepository Profiles,
                                         ILibraryIndexRepository Indexes,
                                         IExcludedSymbolsRepository Excluded,
                                         IDiffRepository Diffs,
                                         IPageRepository Pages,
                                         IChunkRepository Chunks,
                                         IBm25ShardRepository Bm25,
                                         ILibraryDeletionService Deletion,
                                        ILibraryIngestionModeLeaseManager ModeManager,
                                         ILibraryIngestionModeRepository Modes,
                                         ILibraryIngestionModeLease ModeLease,
                                         IDirectoryPublicationLease? PublicationLease,
                                         IReembedJobDispatcher ReembedDispatcher);

    private sealed record Bm25PackageData(
        LibraryIndex Index,
        IReadOnlyList<Bm25Shard> Shards,
        IReadOnlyDictionary<string, byte[]> GridFsPayloads);

    private static LibraryDeletionResult EmptyDeletionResult => new(0,
                                                                     0,
                                                                     0,
                                                                     0,
                                                                     0,
                                                                     0,
                                                                     0,
                                                                     0,
                                                                     0);

    public enum PackageRecordType
    {
        SourceDocument,
        LibraryVersion,
        Profile,
        Index,
        Diff,
        ExcludedSymbol,
        Page,
        Chunk
    }

    public enum InvalidIdentityPart
    {
        Library,
        Version,
        Id
    }

    public enum Bm25Corruption
    {
        ValidExternalPayload,
        InlineForeignChunk,
        ExternalForeignChunk,
        ExternalMalformedPayload,
        WholeShardForeignChunk,
        WrongShardRouting,
        DocumentCountMismatch
    }

    public enum InvalidSubjectCatalogShape
    {
        EmptyCatalog,
        NullConceptList,
        NullConcept,
        MissingLabel,
        MissingDescription,
        NullAliasList,
        BlankAlias,
        DuplicateAlias,
        CandidateCatalog
    }

    private const string LibraryId = "directory-lifecycle";
    private const string Version = "1.0";
    private const string LaterVersion = "1.1";
    private const string PreviousVersion = "0.9";
    private const string ForeignLibraryId = "foreign-library";
    private const string ForeignVersion = "9.9";
    private const string InvalidRecordId = "malformed-record-id";
    private const string ForeignRecordId = "foreign-library/9.9/foreign-row";
    private const string ExcludedName = "noise";
    private const string SourceRelativePath = "manuals/test.pdf";
    private const string SubjectTaxonomyVersion = "taxonomy-import";
    private const string SubjectScanRunId = "scan-directory-lifecycle-1.0";
    private const string PrimarySubjectId = "hydraulics";
    private const string SecondarySubjectId = "safety";
    private const string DanglingSubjectId = "missing-subject";
    private static readonly DateTime SubjectRecordedAtUtc = new(2026,
                                                                 8,
                                                                 8,
                                                                 12,
                                                                 0,
                                                                 0,
                                                                 DateTimeKind.Utc);
}
