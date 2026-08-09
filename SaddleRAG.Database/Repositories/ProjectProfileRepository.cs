// ProjectProfileRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using MongoDB.Driver;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Database.Repositories;

/// <summary>
///     MongoDB lifecycle operations for project profiles.
/// </summary>
public sealed class ProjectProfileRepository : IProjectProfileRepository
{
    public ProjectProfileRepository(SaddleRagDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        mContext = context;
    }

    private readonly SaddleRagDbContext mContext;

    /// <inheritdoc />
    public async Task<long> CountIngestedPackageReferencesAsync(string libraryId,
                                                                CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        FilterDefinition<ProjectProfile> filter = Builders<ProjectProfile>.Filter.AnyEq(
            profile => profile.IngestedPackages,
            libraryId);
        return await mContext.ProjectProfiles.CountDocumentsAsync(filter, cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task<long> RemoveIngestedPackageAsync(string libraryId,
                                                       CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        FilterDefinition<ProjectProfile> filter = Builders<ProjectProfile>.Filter.AnyEq(
            profile => profile.IngestedPackages,
            libraryId);
        UpdateDefinition<ProjectProfile> update = Builders<ProjectProfile>.Update.Pull(
            profile => profile.IngestedPackages,
            libraryId);
        UpdateResult result = await mContext.ProjectProfiles.UpdateManyAsync(filter, update, cancellationToken: ct);
        return result.ModifiedCount;
    }
}
