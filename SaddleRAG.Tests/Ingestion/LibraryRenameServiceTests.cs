// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Services;

namespace SaddleRAG.Tests.Ingestion;

public sealed class LibraryRenameServiceTests
{
    [Fact]
    public async Task LibraryRenameKeepsBothMarkersUntilMongoAndVectorMaintenanceCommit()
    {
        RenameFixture fixture = MakeNewLibraryRenameFixture();

        RenameLibraryResponse result = await fixture.Service.RenameLibraryAsync(Profile,
                                                                                  SourceLibraryId,
                                                                                  TargetLibraryId,
                                                                                  TestContext.Current
                                                                                             .CancellationToken);

        Assert.Equal(RenameLibraryOutcome.Renamed, result.Outcome);
        RenameLibraryResult expectedCounts = ExpectedFinalCounts(TargetLibraryId, VersionId);
        Assert.Equal(expectedCounts, result.Counts);
        Assert.Null(result.Warning);
        Assert.Equal([
                         "source-commit",
                         "begin-operation",
                         "source-pending",
                         "target-pending",
                         "prepare-directory",
                         "source-pending",
                         "target-pending",
                         "prepare-directory",
                         "apply-mongo",
                         "mongo-committed",
                         "target-commit",
                         "rebuild-target",
                         "remove-source-vector",
                         "vector-committed",
                         "source-pending",
                         "target-pending",
                         "finalize-directory",
                         "target-commit",
                         "target-clear",
                         "source-delete",
                         "delete-operation"
                     ],
                     fixture.Events);
    }

    [Fact]
    public async Task VectorFailureLeavesMongoCheckpointAndExactMarkersForRetry()
    {
        RenameFixture fixture = MakeNewLibraryRenameFixture();
        fixture.Vector.RemoveLibraryIndexesAsync(Profile,
                                                  SourceLibraryId,
                                                  Arg.Any<CancellationToken>())
               .Returns(_ => throw new InvalidOperationException(VectorFailure));

        RenameLibraryResponse result = await fixture.Service.RenameLibraryAsync(Profile,
                                                                                  SourceLibraryId,
                                                                                  TargetLibraryId,
                                                                                  TestContext.Current
                                                                                             .CancellationToken);

        Assert.Equal(RenameLibraryOutcome.Renamed, result.Outcome);
        Assert.Equal(Counts, result.Counts);
        Assert.Contains(VectorFailure, Assert.IsType<string>(result.Warning), StringComparison.Ordinal);
        await fixture.Data.DidNotReceive()
                     .FinalizeDirectoryDefinitionsAsync(Arg.Any<LibraryRenameOperationRecord>(),
                                                        Arg.Any<CancellationToken>());
        await fixture.SourceLease.DidNotReceive()
                     .TryDeleteOwnershipAsync(Arg.Any<CancellationToken>());
        await fixture.Operations.DidNotReceive()
                     .TryDeleteAsync(Arg.Any<string>(),
                                     Arg.Any<string>(),
                                     Arg.Any<LibraryRenameOperationState>(),
                                     Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MongoCommittedRecoveryReacquiresOnlyTheExactPendingOperation()
    {
        LibraryRenameOperationRecord operation = Operation(LibraryRenameOperationState.MongoCommitted,
                                                            Counts);
        RenameFixture fixture = MakeRecoveryFixture(operation,
                                                    sourcePending: true,
                                                    targetPending: true,
                                                    finalized: false);

        RenameLibraryResponse result = await fixture.Service.RenameLibraryAsync(Profile,
                                                                                  SourceLibraryId,
                                                                                  TargetLibraryId,
                                                                                  TestContext.Current
                                                                                             .CancellationToken);

        Assert.Equal(RenameLibraryOutcome.Renamed, result.Outcome);
        RenameLibraryResult expectedCounts = ExpectedFinalCounts(TargetLibraryId, VersionId);
        Assert.Equal(expectedCounts, result.Counts);
        await fixture.Operations.Received(requiredNumberOfCalls: 1)
                     .TryAdvanceAsync(SourceLibraryId,
                                      OperationId,
                                      LibraryRenameOperationState.MongoCommitted,
                                      LibraryRenameOperationState.VectorCommitted,
                                      expectedCounts,
                                      Arg.Any<DateTime>(),
                                      Arg.Any<CancellationToken>());
        await fixture.ModeManager.Received(requiredNumberOfCalls: 1)
                     .TryAcquireRenameRecoveryAsync(Profile,
                                                    SourceLibraryId,
                                                    LibraryIngestionMode.Web,
                                                    OperationId,
                                                    Arg.Any<CancellationToken>());
        await fixture.ModeManager.Received(requiredNumberOfCalls: 1)
                     .TryAcquireRenameRecoveryAsync(Profile,
                                                    TargetLibraryId,
                                                    LibraryIngestionMode.Web,
                                                    OperationId,
                                                    Arg.Any<CancellationToken>());
        await fixture.Data.DidNotReceive()
                     .ApplyLibraryRenameAsync(Arg.Any<LibraryRenameOperationRecord>(),
                                              Arg.Any<CancellationToken>());
        Assert.Contains("rebuild-target", fixture.Events);
        Assert.Contains("delete-operation", fixture.Events);
    }

    [Fact]
    public async Task MongoCommittedVersionRecoveryPersistsActualRebuiltShardCount()
    {
        LibraryRenameOperationRecord operation = VersionOperation(LibraryRenameOperationState.MongoCommitted,
                                                                   Counts);
        RenameFixture fixture = MakeVersionRecoveryFixture(operation);

        RenameLibraryResponse result = await fixture.Service.RenameVersionAsync(Profile,
                                                                                  SourceLibraryId,
                                                                                  SourceVersionId,
                                                                                  TargetVersionId,
                                                                                  TestContext.Current
                                                                                             .CancellationToken);

        RenameLibraryResult expectedCounts = ExpectedFinalCounts(SourceLibraryId, TargetVersionId);
        Assert.Equal(RenameLibraryOutcome.Renamed, result.Outcome);
        Assert.Equal(expectedCounts, result.Counts);
        await fixture.Operations.Received(requiredNumberOfCalls: 1)
                     .TryAdvanceAsync(SourceLibraryId,
                                      OperationId,
                                      LibraryRenameOperationState.MongoCommitted,
                                      LibraryRenameOperationState.VectorCommitted,
                                      expectedCounts,
                                      Arg.Any<DateTime>(),
                                      Arg.Any<CancellationToken>());
        await fixture.Vector.Received(requiredNumberOfCalls: 1)
                     .RemoveIndexAsync(Profile,
                                       SourceLibraryId,
                                       SourceVersionId,
                                       Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CrashAfterSourceOwnershipDeletionOnlyDeletesVerifiedCompletedOperation()
    {
        LibraryRenameOperationRecord operation = Operation(LibraryRenameOperationState.VectorCommitted,
                                                            Counts);
        RenameFixture fixture = MakeRecoveryFixture(operation,
                                                    sourcePending: false,
                                                    targetPending: false,
                                                    finalized: true,
                                                    sourceOwnershipMissing: true);

        RenameLibraryResponse result = await fixture.Service.RenameLibraryAsync(Profile,
                                                                                  SourceLibraryId,
                                                                                  TargetLibraryId,
                                                                                  TestContext.Current
                                                                                             .CancellationToken);

        Assert.Equal(RenameLibraryOutcome.Renamed, result.Outcome);
        await fixture.ModeManager.DidNotReceive()
                     .TryAcquireAsync(Profile,
                                      SourceLibraryId,
                                      Arg.Any<LibraryIngestionMode>(),
                                      Arg.Any<CancellationToken>());
        await fixture.Data.Received(requiredNumberOfCalls: 2)
                     .IsFinalizedAsync(operation, Arg.Any<CancellationToken>());
        await fixture.Data.DidNotReceive()
                     .FinalizeDirectoryDefinitionsAsync(Arg.Any<LibraryRenameOperationRecord>(),
                                                        Arg.Any<CancellationToken>());
        Assert.Equal(["delete-operation"], fixture.Events);
    }

    [Fact]
    public async Task ClearedTargetMarkerIsRearmedBeforeDeletingSourceOwnership()
    {
        LibraryRenameOperationRecord operation = Operation(LibraryRenameOperationState.VectorCommitted,
                                                            Counts);
        RenameFixture fixture = MakeRecoveryFixture(operation,
                                                    sourcePending: true,
                                                    targetPending: false,
                                                    finalized: false);

        _ = await fixture.Service.RenameLibraryAsync(Profile,
                                                     SourceLibraryId,
                                                     TargetLibraryId,
                                                     TestContext.Current.CancellationToken);

        Received.InOrder(() =>
                         {
                             fixture.TargetLease.TryMarkPendingRenameAsync(OperationId,
                                                                            Arg.Any<CancellationToken>());
                             fixture.Data.FinalizeDirectoryDefinitionsAsync(
                                  Arg.Is<LibraryRenameOperationRecord>(item =>
                                      item != null &&
                                      item.State == LibraryRenameOperationState.VectorCommitted),
                                 Arg.Any<CancellationToken>());
                             fixture.TargetLease.TryClearPendingRenameAsync(OperationId,
                                                                             Arg.Any<CancellationToken>());
                             fixture.SourceLease.TryDeleteOwnershipAsync(Arg.Any<CancellationToken>());
                         });
    }

    [Fact]
    public async Task NewerSourceOwnershipGenerationIsNeverMarkedOrDeletedByRecovery()
    {
        LibraryRenameOperationRecord operation = Operation(LibraryRenameOperationState.VectorCommitted,
                                                            Counts);
        RenameFixture fixture = MakeRecoveryFixture(operation,
                                                    sourcePending: false,
                                                    targetPending: false,
                                                    finalized: false);
        fixture.Modes.GetAsync(SourceLibraryId, Arg.Any<CancellationToken>())
               .Returns(Ownership(SourceLibraryId, pendingOperationId: null) with
                            {
                                ReservedAtUtc = RecordedAt.AddMinutes(1)
                            });

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.RenameLibraryAsync(Profile,
                                               SourceLibraryId,
                                               TargetLibraryId,
                                               TestContext.Current.CancellationToken));

        Assert.Contains("newer generation", exception.Message, StringComparison.Ordinal);
        await fixture.SourceLease.DidNotReceive()
                     .TryMarkPendingRenameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.SourceLease.DidNotReceive()
                     .TryDeleteOwnershipAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DifferentRecoveryTargetIsRejectedBeforeAnyLeaseAcquisition()
    {
        LibraryRenameOperationRecord operation = Operation(LibraryRenameOperationState.Applying,
                                                            counts: null);
        RenameFixture fixture = MakeRecoveryFixture(operation,
                                                    sourcePending: true,
                                                    targetPending: true,
                                                    finalized: false);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.RenameLibraryAsync(Profile,
                                               SourceLibraryId,
                                               DifferentTargetLibraryId,
                                               TestContext.Current.CancellationToken));

        Assert.Contains("different rename", exception.Message, StringComparison.Ordinal);
        await fixture.ModeManager.DidNotReceiveWithAnyArgs()
                     .TryAcquireAsync(default,
                                      default!,
                                      default,
                                      TestContext.Current.CancellationToken);
        await fixture.ModeManager.DidNotReceiveWithAnyArgs()
                     .TryAcquireRenameRecoveryAsync(default,
                                                    default!,
                                                    default,
                                                    default!,
                                                    TestContext.Current.CancellationToken);
    }

    private static RenameFixture MakeNewLibraryRenameFixture()
    {
        RenameFixture fixture = MakeBaseFixture();
        fixture.Operations.GetAsync(SourceLibraryId, Arg.Any<CancellationToken>())
               .Returns((LibraryRenameOperationRecord?)null);
        fixture.Modes.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns((LibraryIngestionModeRecord?)null);
        fixture.Modes.GetAsync(SourceLibraryId, Arg.Any<CancellationToken>())
               .Returns((LibraryIngestionModeRecord?)null,
                        (LibraryIngestionModeRecord?)null,
                        Ownership(SourceLibraryId, pendingOperationId: null));
        fixture.Modes.GetAsync(TargetLibraryId, Arg.Any<CancellationToken>())
               .Returns((LibraryIngestionModeRecord?)null,
                        Ownership(TargetLibraryId, pendingOperationId: null) with
                            {
                                OwnershipState = LibraryIngestionOwnershipState.Reserved,
                                CommittedAtUtc = null
                            });
        fixture.Sources.GetDirectoryDefinitionAsync(SourceLibraryId, Arg.Any<CancellationToken>())
               .Returns((DirectoryLibraryDefinition?)null);
        fixture.ModeManager.TryAcquireAsync(Profile,
                                            SourceLibraryId,
                                            LibraryIngestionMode.Web,
                                            Arg.Any<CancellationToken>())
               .Returns(fixture.SourceLease);
        fixture.ModeManager.TryAcquireAsync(Profile,
                                            TargetLibraryId,
                                            LibraryIngestionMode.Web,
                                            Arg.Any<CancellationToken>())
               .Returns(fixture.TargetLease);
        fixture.SourceLease.OwnershipStateAtAcquisition.Returns(LibraryIngestionOwnershipState.Reserved);
        fixture.TargetLease.OwnershipStateAtAcquisition.Returns(LibraryIngestionOwnershipState.Reserved);
        fixture.SourceLease.TryCommitAsync(Arg.Any<CancellationToken>())
               .Returns(_ =>
                        {
                            fixture.Events.Add("source-commit");
                            return true;
                        });
        fixture.Data.PreflightLibraryRenameAsync(SourceLibraryId,
                                                 TargetLibraryId,
                                                 Arg.Any<CancellationToken>())
               .Returns(RenameLibraryOutcome.Renamed);
        fixture.Operations.TryBeginAsync(Arg.Any<LibraryRenameOperationRecord>(),
                                         Arg.Any<CancellationToken>())
               .Returns(call =>
                        {
                            fixture.Events.Add("begin-operation");
                            return call.Arg<LibraryRenameOperationRecord>();
                        });
        ConfigureApplyingAndFinalization(fixture);
        return fixture;
    }

    private static RenameFixture MakeRecoveryFixture(LibraryRenameOperationRecord operation,
                                                     bool sourcePending,
                                                     bool targetPending,
                                                     bool finalized,
                                                     bool sourceOwnershipMissing = false)
    {
        RenameFixture fixture = MakeBaseFixture();
        fixture.Operations.GetAsync(SourceLibraryId, Arg.Any<CancellationToken>()).Returns(operation);
        LibraryIngestionModeRecord? source = sourceOwnershipMissing
                                                 ? null
                                                 : Ownership(SourceLibraryId,
                                                             sourcePending ? OperationId : null);
        LibraryIngestionModeRecord target = Ownership(TargetLibraryId,
                                                       targetPending ? OperationId : null);
        fixture.Modes.GetAsync(SourceLibraryId, Arg.Any<CancellationToken>()).Returns(source);
        fixture.Modes.GetAsync(TargetLibraryId, Arg.Any<CancellationToken>()).Returns(target);
        if (!sourceOwnershipMissing)
        {
            fixture.ModeManager.TryAcquireRenameRecoveryAsync(Profile,
                                                               SourceLibraryId,
                                                               LibraryIngestionMode.Web,
                                                               OperationId,
                                                               Arg.Any<CancellationToken>())
                   .Returns(fixture.SourceLease);
        }

        fixture.ModeManager.TryAcquireRenameRecoveryAsync(Profile,
                                                           TargetLibraryId,
                                                           LibraryIngestionMode.Web,
                                                           OperationId,
                                                           Arg.Any<CancellationToken>())
               .Returns(fixture.TargetLease);
        ConfigureApplyingAndFinalization(fixture);
        fixture.Data.IsFinalizedAsync(Arg.Is<LibraryRenameOperationRecord>(item =>
                                           item != null &&
                                           item.State == LibraryRenameOperationState.VectorCommitted),
                                       Arg.Any<CancellationToken>())
               .Returns(_ => finalized ||
                             fixture.Events.Contains("source-delete", StringComparer.Ordinal));
        return fixture;
    }

    private static RenameFixture MakeBaseFixture()
    {
        var events = new List<string>();
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var operations = Substitute.For<ILibraryRenameOperationRepository>();
        var data = Substitute.For<ILibraryRenameDataRepository>();
        var modes = Substitute.For<ILibraryIngestionModeRepository>();
        var sources = Substitute.For<ISourceDocumentRepository>();
        var libraries = Substitute.For<ILibraryRepository>();
        var chunks = Substitute.For<IChunkRepository>();
        var indexes = Substitute.For<ILibraryIndexRepository>();
        var shards = Substitute.For<IBm25ShardRepository>();
        var vector = Substitute.For<IVectorSearchProvider>();
        var manager = Substitute.For<ILibraryIngestionModeLeaseManager>();
        var sourceLease = MakeLease(SourceLibraryId, events, "source");
        var targetLease = MakeLease(TargetLibraryId, events, "target");
        factory.GetLibraryRenameOperationRepository(Profile).Returns(operations);
        factory.GetLibraryRenameDataRepository(Profile).Returns(data);
        factory.GetLibraryIngestionModeRepository(Profile).Returns(modes);
        factory.GetSourceDocumentRepository(Profile).Returns(sources);
        factory.GetLibraryRepository(Profile).Returns(libraries);
        factory.GetChunkRepository(Profile).Returns(chunks);
        factory.GetLibraryIndexRepository(Profile).Returns(indexes);
        factory.GetBm25ShardRepository(Profile).Returns(shards);
        libraries.GetVersionsAsync(TargetLibraryId, Arg.Any<CancellationToken>())
                 .Returns([Version(TargetLibraryId, VersionId)]);
        chunks.GetChunksAsync(TargetLibraryId, VersionId, Arg.Any<CancellationToken>())
              .Returns([Chunk(TargetLibraryId, VersionId)]);
        chunks.GetChunksAsync(SourceLibraryId, TargetVersionId, Arg.Any<CancellationToken>())
              .Returns([Chunk(SourceLibraryId, TargetVersionId)]);
        vector.IndexChunksAsync(Profile,
                                TargetLibraryId,
                                VersionId,
                                Arg.Any<IReadOnlyList<DocChunk>>(),
                                Arg.Any<CancellationToken>())
              .Returns(_ =>
                       {
                           events.Add("rebuild-target");
                           return Task.CompletedTask;
                       });
        vector.IndexChunksAsync(Profile,
                                SourceLibraryId,
                                TargetVersionId,
                                Arg.Any<IReadOnlyList<DocChunk>>(),
                                Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);
        vector.RemoveLibraryIndexesAsync(Profile,
                                          SourceLibraryId,
                                          Arg.Any<CancellationToken>())
              .Returns(_ =>
                       {
                           events.Add("remove-source-vector");
                           return Task.CompletedTask;
                       });
        vector.RemoveIndexAsync(Profile,
                                SourceLibraryId,
                                SourceVersionId,
                                Arg.Any<CancellationToken>())
              .Returns(_ =>
                       {
                           events.Add("remove-source-version-vector");
                           return Task.CompletedTask;
                       });
        ILibraryRenameService service = new LibraryRenameService(factory,
                                                                  vector,
                                                                  manager,
                                                                  TimeProvider.System);
        return new RenameFixture(service,
                                 operations,
                                 data,
                                 modes,
                                 sources,
                                 vector,
                                 manager,
                                 sourceLease,
                                 targetLease,
                                 events);
    }

    private static void ConfigureApplyingAndFinalization(RenameFixture fixture)
    {
        RenameLibraryResult expectedCounts = ExpectedFinalCounts(TargetLibraryId, VersionId);
        fixture.Data.PrepareDirectoryDefinitionsAsync(Arg.Any<LibraryRenameOperationRecord>(),
                                                      Arg.Any<CancellationToken>())
               .Returns(_ =>
                        {
                            fixture.Events.Add("prepare-directory");
                            return Task.CompletedTask;
                        });
        fixture.Data.ApplyLibraryRenameAsync(Arg.Any<LibraryRenameOperationRecord>(),
                                             Arg.Any<CancellationToken>())
               .Returns(_ =>
                        {
                            fixture.Events.Add("apply-mongo");
                            return Counts;
                        });
        fixture.Operations.TryAdvanceAsync(SourceLibraryId,
                                           Arg.Any<string>(),
                                           LibraryRenameOperationState.Applying,
                                           LibraryRenameOperationState.MongoCommitted,
                                           Counts,
                                           Arg.Any<DateTime>(),
                                           Arg.Any<CancellationToken>())
               .Returns(_ =>
                        {
                            fixture.Events.Add("mongo-committed");
                            return true;
                        });
        fixture.Operations.TryAdvanceAsync(SourceLibraryId,
                                           Arg.Any<string>(),
                                           LibraryRenameOperationState.MongoCommitted,
                                           LibraryRenameOperationState.VectorCommitted,
                                           expectedCounts,
                                           Arg.Any<DateTime>(),
                                           Arg.Any<CancellationToken>())
               .Returns(_ =>
                        {
                            fixture.Events.Add("vector-committed");
                            return true;
                        });
        fixture.Data.IsFinalizedAsync(Arg.Any<LibraryRenameOperationRecord>(),
                                      Arg.Any<CancellationToken>())
               .Returns(_ => fixture.Events.Contains("source-delete", StringComparer.Ordinal));
        fixture.Data.FinalizeDirectoryDefinitionsAsync(Arg.Any<LibraryRenameOperationRecord>(),
                                                       Arg.Any<CancellationToken>())
               .Returns(_ =>
                        {
                            fixture.Events.Add("finalize-directory");
                            return Task.CompletedTask;
                        });
        fixture.Operations.TryDeleteAsync(SourceLibraryId,
                                          Arg.Any<string>(),
                                          LibraryRenameOperationState.VectorCommitted,
                                          Arg.Any<CancellationToken>())
               .Returns(_ =>
                        {
                            fixture.Events.Add("delete-operation");
                            return true;
                        });
    }

    private static RenameFixture MakeVersionRecoveryFixture(LibraryRenameOperationRecord operation)
    {
        RenameFixture fixture = MakeBaseFixture();
        RenameLibraryResult expectedCounts = ExpectedFinalCounts(SourceLibraryId, TargetVersionId);
        fixture.Operations.GetAsync(SourceLibraryId, Arg.Any<CancellationToken>()).Returns(operation);
        fixture.Modes.GetAsync(SourceLibraryId, Arg.Any<CancellationToken>())
               .Returns(Ownership(SourceLibraryId, OperationId));
        fixture.ModeManager.TryAcquireRenameRecoveryAsync(Profile,
                                                           SourceLibraryId,
                                                           LibraryIngestionMode.Web,
                                                           OperationId,
                                                           Arg.Any<CancellationToken>())
               .Returns(fixture.SourceLease);
        fixture.Operations.TryAdvanceAsync(SourceLibraryId,
                                           OperationId,
                                           LibraryRenameOperationState.MongoCommitted,
                                           LibraryRenameOperationState.VectorCommitted,
                                           expectedCounts,
                                           Arg.Any<DateTime>(),
                                           Arg.Any<CancellationToken>())
               .Returns(true);
        fixture.Data.IsFinalizedAsync(Arg.Is<LibraryRenameOperationRecord>(item =>
                                           item != null &&
                                           item.State == LibraryRenameOperationState.VectorCommitted),
                                       Arg.Any<CancellationToken>())
               .Returns(_ => fixture.Events.Contains("source-clear", StringComparer.Ordinal));
        fixture.Data.FinalizeDirectoryDefinitionsAsync(Arg.Any<LibraryRenameOperationRecord>(),
                                                       Arg.Any<CancellationToken>())
               .Returns(Task.CompletedTask);
        fixture.Operations.TryDeleteAsync(SourceLibraryId,
                                          OperationId,
                                          LibraryRenameOperationState.VectorCommitted,
                                          Arg.Any<CancellationToken>())
               .Returns(true);
        return fixture;
    }

    private static ILibraryIngestionModeLease MakeLease(string libraryId,
                                                        ICollection<string> events,
                                                        string label)
    {
        var result = Substitute.For<ILibraryIngestionModeLease>();
        result.LibraryId.Returns(libraryId);
        result.Mode.Returns(LibraryIngestionMode.Web);
        result.OwnershipStateAtAcquisition.Returns(LibraryIngestionOwnershipState.Committed);
        result.OwnershipLostToken.Returns(CancellationToken.None);
        result.TryCommitAsync(Arg.Any<CancellationToken>())
              .Returns(_ =>
                       {
                           events.Add($"{label}-commit");
                           return true;
                       });
        result.TryMarkPendingRenameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(_ =>
                       {
                           events.Add($"{label}-pending");
                           return true;
                       });
        result.TryClearPendingRenameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(_ =>
                       {
                           events.Add($"{label}-clear");
                           return true;
                       });
        result.TryDeleteOwnershipAsync(Arg.Any<CancellationToken>())
              .Returns(_ =>
                       {
                           events.Add($"{label}-delete");
                           return true;
                       });
        return result;
    }

    private static LibraryRenameOperationRecord Operation(LibraryRenameOperationState state,
                                                          RenameLibraryResult? counts) =>
        new()
            {
                Id = SourceLibraryId,
                OperationId = OperationId,
                Kind = LibraryRenameOperationKind.Library,
                State = state,
                Mode = LibraryIngestionMode.Web,
                SourceLibraryId = SourceLibraryId,
                TargetLibraryId = TargetLibraryId,
                SourceOwnershipReservedAtUtc = RecordedAt,
                TargetOwnershipReservedAtUtc = RecordedAt,
                Counts = counts,
                StartedAtUtc = RecordedAt,
                UpdatedAtUtc = RecordedAt
            };

    private static LibraryRenameOperationRecord VersionOperation(LibraryRenameOperationState state,
                                                                 RenameLibraryResult? counts) =>
        new()
            {
                Id = SourceLibraryId,
                OperationId = OperationId,
                Kind = LibraryRenameOperationKind.Version,
                State = state,
                Mode = LibraryIngestionMode.Web,
                SourceLibraryId = SourceLibraryId,
                TargetLibraryId = SourceLibraryId,
                SourceVersion = SourceVersionId,
                TargetVersion = TargetVersionId,
                SourceOwnershipReservedAtUtc = RecordedAt,
                TargetOwnershipReservedAtUtc = RecordedAt,
                Counts = counts,
                StartedAtUtc = RecordedAt,
                UpdatedAtUtc = RecordedAt
            };

    private static LibraryIngestionModeRecord Ownership(string libraryId, string? pendingOperationId) =>
        new()
            {
                Id = libraryId,
                Mode = LibraryIngestionMode.Web,
                OwnershipState = LibraryIngestionOwnershipState.Committed,
                PendingRenameOperationId = pendingOperationId,
                ReservedAtUtc = RecordedAt,
                CommittedAtUtc = RecordedAt,
                UpdatedAtUtc = RecordedAt
            };

    private static LibraryVersionRecord Version(string libraryId, string version) => new()
        {
            Id = $"{libraryId}/{version}",
            LibraryId = libraryId,
            Version = version,
            ScrapedAt = RecordedAt,
            PageCount = 1,
            ChunkCount = 1,
            EmbeddingProviderId = "test",
            EmbeddingModelName = "test",
            EmbeddingDimensions = 2
        };

    private static DocChunk Chunk(string libraryId, string version) => new()
        {
            Id = $"{libraryId}/{version}/chunk",
            LibraryId = libraryId,
            Version = version,
            PageUrl = "https://example.test/page",
            PageTitle = "Page",
            Category = DocCategory.HowTo,
            Content = "content",
            TokenCount = 1,
            Embedding = [1.0f, 0.0f]
        };

    private static RenameLibraryResult ExpectedFinalCounts(string libraryId, string version) =>
        Counts with
            {
                Bm25Shards = Bm25IndexBuilder.Build(libraryId, version, [Chunk(libraryId, version)]).Shards.Count
            };

    private sealed record RenameFixture(ILibraryRenameService Service,
                                        ILibraryRenameOperationRepository Operations,
                                        ILibraryRenameDataRepository Data,
                                        ILibraryIngestionModeRepository Modes,
                                        ISourceDocumentRepository Sources,
                                        IVectorSearchProvider Vector,
                                        ILibraryIngestionModeLeaseManager ModeManager,
                                        ILibraryIngestionModeLease SourceLease,
                                        ILibraryIngestionModeLease TargetLease,
                                        List<string> Events);

    private static readonly RenameLibraryResult Counts = new(Libraries: 1,
                                                              Versions: 1,
                                                              Chunks: 1,
                                                              Pages: 1,
                                                              Profiles: 0,
                                                              Indexes: 0,
                                                              Bm25Shards: 0,
                                                              ExcludedSymbols: 0,
                                                              ScrapeJobs: 0);
    private static readonly DateTime RecordedAt = new(year: 2026,
                                                      month: 8,
                                                      day: 8,
                                                      hour: 12,
                                                      minute: 0,
                                                      second: 0,
                                                      DateTimeKind.Utc);
    private const string Profile = "profile-a";
    private const string SourceLibraryId = "manual-library";
    private const string TargetLibraryId = "renamed-library";
    private const string DifferentTargetLibraryId = "different-library";
    private const string VersionId = "v1";
    private const string SourceVersionId = "old-version";
    private const string TargetVersionId = "new-version";
    private const string OperationId = "operation-1";
    private const string VectorFailure = "vector maintenance failed";
}
