// McpWarmupArtifactRecoveryTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class McpWarmupArtifactRecoveryTests
{
    [Fact]
    public async Task StartupRecoveryRunsForRequestedProfileAndReturnsCounts()
    {
        var repositoryFactory = Substitute.For<RepositoryFactory>([null!]);
        var sourceDocuments = Substitute.For<ISourceDocumentRepository>();
        var expected = new DocumentArtifactRecoveryResult(3, 2, 1);
        repositoryFactory.GetSourceDocumentRepository(ProfileName).Returns(sourceDocuments);
        sourceDocuments.RecoverArtifactClaimsAsync(
                           Arg.Is<DateTime>(value => value.Kind == DateTimeKind.Utc),
                           TestContext.Current.CancellationToken)
                       .Returns(expected);

        DocumentArtifactRecoveryResult? result =
            await McpWarmupService.RecoverArtifactClaimsForProfileAsync(
                ProfileName,
                repositoryFactory,
                NullLogger<McpWarmupService>.Instance,
                TestContext.Current.CancellationToken);

        Assert.Equal(expected, result);
        repositoryFactory.Received(requiredNumberOfCalls: 1)
                         .GetSourceDocumentRepository(ProfileName);
        await sourceDocuments.Received(requiredNumberOfCalls: 1)
                             .RecoverArtifactClaimsAsync(
                                 Arg.Is<DateTime>(value => value.Kind == DateTimeKind.Utc),
                                 TestContext.Current.CancellationToken);
    }

    private const string ProfileName = "artifact-recovery-profile";
}
