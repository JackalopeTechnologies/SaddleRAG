// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using MongoDB.Driver;

namespace SaddleRAG.Database.Repositories;

/// <summary>MongoDB persistence for document subject assignments.</summary>
public sealed class SubjectAssignmentRepository : ISubjectAssignmentRepository
{
    public SubjectAssignmentRepository(SaddleRagDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        mContext = context;
    }

    private readonly SaddleRagDbContext mContext;

    public Task PersistAsync(SubjectAssignmentRecord assignment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var filter = Builders<SubjectAssignmentRecord>.Filter.Eq(item => item.Id, assignment.Id);
        return mContext.SubjectAssignments.ReplaceOneAsync(filter,
                                                           assignment,
                                                           new ReplaceOptions { IsUpsert = true },
                                                           ct);
    }

    public Task<IReadOnlyList<SubjectAssignmentRecord>> GetByDocumentRevisionIdsAsync(
        IReadOnlyCollection<string> documentRevisionIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(documentRevisionIds);
        Task<IReadOnlyList<SubjectAssignmentRecord>> result;
        if (documentRevisionIds.Count == 0)
            result = Task.FromResult<IReadOnlyList<SubjectAssignmentRecord>>([]);
        else
        {
            var filter = Builders<SubjectAssignmentRecord>.Filter.In(item => item.DocumentRevisionId,
                                                                      documentRevisionIds);
            result = GetManyCoreAsync(filter, ct);
        }

        return result;
    }

    public Task<long> DeleteScanRunAsync(string libraryId, string scanRunId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);
        return DeleteScanRunCoreAsync(libraryId, scanRunId, ct);
    }

    public async Task<long> DeleteLibraryAsync(string libraryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        DeleteResult result = await mContext.SubjectAssignments.DeleteManyAsync(
                                  assignment => assignment.LibraryId == libraryId,
                                  ct);
        return result.DeletedCount;
    }

    public static string MakeId(string libraryId, string version, string documentRevisionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(documentRevisionId);
        return $"{libraryId}/{version}/{documentRevisionId}/subjects";
    }

    private async Task<IReadOnlyList<SubjectAssignmentRecord>> GetManyCoreAsync(
        FilterDefinition<SubjectAssignmentRecord> filter,
        CancellationToken ct)
    {
        var result = await mContext.SubjectAssignments.Find(filter).ToListAsync(ct);
        return result;
    }

    private async Task<long> DeleteScanRunCoreAsync(string libraryId,
                                                    string scanRunId,
                                                    CancellationToken ct)
    {
        DeleteResult result = await mContext.SubjectAssignments.DeleteManyAsync(
                                  assignment => assignment.LibraryId == libraryId &&
                                                assignment.ScanRunId == scanRunId,
                                  ct);
        return result.DeletedCount;
    }
}
