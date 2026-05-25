// ParameterAliasReconcilerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging;
using NSubstitute;
using SaddleRAG.Ingestion.Reconciliation;

#endregion

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     Verifies <see cref="ParameterAliasReconciler.Resolve{T}" /> across
///     the five branches of its decision matrix: neither value provided,
///     only the new name, only the old name, both names equal, and both
///     names differing.
/// </summary>
public sealed class ParameterAliasReconcilerTests
{
    [Fact]
    public void NeitherValueProvidedReturnsDefault()
    {
        var logger = Substitute.For<ILogger>();
        var log = new DeprecationLog();

        string? result = ParameterAliasReconciler.Resolve<string>(newValue: null,
                                                                  oldValue: null,
                                                                  NewName,
                                                                  OldName,
                                                                  logger,
                                                                  log);

        Assert.Null(result);
        AssertNoWarning(logger);
    }

    [Fact]
    public void OnlyNewValueProvidedReturnsItWithoutWarning()
    {
        var logger = Substitute.For<ILogger>();
        var log = new DeprecationLog();

        string? result = ParameterAliasReconciler.Resolve(newValue: NewValue,
                                                          oldValue: (string?) null,
                                                          NewName,
                                                          OldName,
                                                          logger,
                                                          log);

        Assert.Equal(NewValue, result);
        AssertNoWarning(logger);
    }

    [Fact]
    public void OnlyOldValueProvidedReturnsItAndWarns()
    {
        var logger = Substitute.For<ILogger>();
        var log = new DeprecationLog();

        string? result = ParameterAliasReconciler.Resolve(newValue: (string?) null,
                                                          oldValue: OldValue,
                                                          NewName,
                                                          OldName,
                                                          logger,
                                                          log);

        Assert.Equal(OldValue, result);
        AssertOneWarning(logger);
    }

    [Fact]
    public void BothProvidedEqualReturnsNewWithoutWarning()
    {
        var logger = Substitute.For<ILogger>();
        var log = new DeprecationLog();

        string? result = ParameterAliasReconciler.Resolve(newValue: NewValue,
                                                          oldValue: NewValue,
                                                          NewName,
                                                          OldName,
                                                          logger,
                                                          log);

        Assert.Equal(NewValue, result);
        AssertNoWarning(logger);
    }

    [Fact]
    public void BothProvidedDifferingThrows()
    {
        var logger = Substitute.For<ILogger>();
        var log = new DeprecationLog();

        var ex = Assert.Throws<ArgumentException>(
                                                  () => ParameterAliasReconciler.Resolve(newValue: NewValue,
                                                            oldValue: OldValue,
                                                            NewName,
                                                            OldName,
                                                            logger,
                                                            log));

        Assert.Contains(NewName, ex.Message);
        Assert.Contains(OldName, ex.Message);
    }

    [Fact]
    public void EmptyStringInNewIsTreatedAsAbsentAndPrefersOld()
    {
        var logger = Substitute.For<ILogger>();
        var log = new DeprecationLog();

        // Mirrors the real MCP signature pattern where the new param has
        // an empty-string default and only the deprecated alias is supplied.
        string? result = ParameterAliasReconciler.Resolve(newValue: string.Empty,
                                                          oldValue: OldValue,
                                                          NewName,
                                                          OldName,
                                                          logger,
                                                          log);

        Assert.Equal(OldValue, result);
        AssertOneWarning(logger);
    }

    [Fact]
    public void RepeatedOldOnlyCallsWarnOncePerProcess()
    {
        var logger = Substitute.For<ILogger>();
        var log = new DeprecationLog();

        for(int i = 0; i < 5; i++)
        {
            ParameterAliasReconciler.Resolve(newValue: (string?) null,
                                             oldValue: OldValue,
                                             NewName,
                                             OldName,
                                             logger,
                                             log);
        }

        AssertOneWarning(logger);
    }

    [Fact]
    public void NullNameThrows()
    {
        var logger = Substitute.For<ILogger>();
        var log = new DeprecationLog();

        Assert.Throws<ArgumentException>(
                                         () => ParameterAliasReconciler.Resolve<string>(newValue: NewValue,
                                                   oldValue: null,
                                                   newName: string.Empty,
                                                   OldName,
                                                   logger,
                                                   log));
        Assert.Throws<ArgumentException>(
                                         () => ParameterAliasReconciler.Resolve<string>(newValue: NewValue,
                                                   oldValue: null,
                                                   NewName,
                                                   oldName: string.Empty,
                                                   logger,
                                                   log));
    }

    private static void AssertOneWarning(ILogger logger)
    {
        logger.Received(requiredNumberOfCalls: 1)
              .Log(LogLevel.Warning,
                   Arg.Any<EventId>(),
                   Arg.Any<object>(),
                   Arg.Any<Exception?>(),
                   Arg.Any<Func<object, Exception?, string>>());
    }

    private static void AssertNoWarning(ILogger logger)
    {
        logger.DidNotReceive()
              .Log(LogLevel.Warning,
                   Arg.Any<EventId>(),
                   Arg.Any<object>(),
                   Arg.Any<Exception?>(),
                   Arg.Any<Func<object, Exception?, string>>());
    }

    private const string NewName = "library";

    private const string OldName = "libraryId";

    private const string NewValue = "automation1-dotnet";

    private const string OldValue = "old-library-id";
}
