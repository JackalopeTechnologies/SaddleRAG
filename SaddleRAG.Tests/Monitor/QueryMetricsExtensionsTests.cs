// QueryMetricsExtensionsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Ingestion.Diagnostics;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class QueryMetricsExtensionsTests
{
    [Fact]
    public async Task TimeAsyncRecordsSuccessSampleAndReturnsResult()
    {
        var rec = new QueryMetricsRecorder(capacity: 16);
        var result = await rec.TimeAsync("search",
                                         () => Task.FromResult(SuccessValue),
                                         resultCount: r => r);
        Assert.Equal(SuccessValue, result);

        var snap = rec.Snapshot();
        var op = Assert.Single(snap.PerOperation);
        Assert.Equal("search", op.Operation);
        Assert.Equal(expected: 1, op.Count);
        Assert.Equal(expected: 0, op.FailureCount);
        Assert.True(op.MaxMs >= 0);
    }

    [Fact]
    public async Task TimeAsyncRecordsFailureSampleAndRethrows()
    {
        var rec = new QueryMetricsRecorder(capacity: 16);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rec.TimeAsync<int>("search",
                                     () => Task.FromException<int>(new InvalidOperationException(FailureMessage))
                                    )
        );
        Assert.Equal(FailureMessage, ex.Message);

        var snap = rec.Snapshot();
        var op = Assert.Single(snap.PerOperation);
        Assert.Equal(expected: 1, op.Count);
        Assert.Equal(expected: 1, op.FailureCount);
    }

    private const int SuccessValue = 42;
    private const string FailureMessage = "boom";
}
