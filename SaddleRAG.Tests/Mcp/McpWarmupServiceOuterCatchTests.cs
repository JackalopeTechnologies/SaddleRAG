// McpWarmupServiceOuterCatchTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Mcp;

#endregion

namespace SaddleRAG.Tests.Mcp;

/// <summary>
///     Locks in the contract that an inner warmup catch which calls
///     <c>MarkFailed</c> with a specific phase suppresses the outer
///     catch's generic re-marking. Without this guard, the outer catch
///     would overwrite <c>PhaseOnnxDownloadFailed</c> with the generic
///     "Failed" phase and double-log the same exception — the exact
///     regression Round 2 fix #1 (commit 4d9e036) closed.
/// </summary>
public sealed class McpWarmupServiceOuterCatchTests
{
    [Fact]
    public void HasInnerCatchAlreadyMarkedFailureReturnsFalseForFreshState()
    {
        var state = new McpWarmupState();

        Assert.False(McpWarmupService.HasInnerCatchAlreadyMarkedFailure(state));
    }

    [Fact]
    public void HasInnerCatchAlreadyMarkedFailureReturnsFalseForRunningState()
    {
        var state = new McpWarmupState();
        state.MarkStarted("Starting");
        state.MarkPhase("ONNX models ready");

        Assert.False(McpWarmupService.HasInnerCatchAlreadyMarkedFailure(state));
    }

    [Fact]
    public void HasInnerCatchAlreadyMarkedFailureReturnsTrueAfterMarkFailed()
    {
        var state = new McpWarmupState();
        state.MarkStarted("Starting");
        state.MarkFailed("ONNX download failed", "boom");

        Assert.True(McpWarmupService.HasInnerCatchAlreadyMarkedFailure(state));
    }

    [Fact]
    public void HasInnerCatchAlreadyMarkedFailureReturnsFalseAfterMarkCompleted()
    {
        var state = new McpWarmupState();
        state.MarkStarted("Starting");
        state.MarkCompleted(nameof(ScrapeJobStatus.Completed));

        Assert.False(McpWarmupService.HasInnerCatchAlreadyMarkedFailure(state));
    }

    [Fact]
    public void MarkFailedFollowedBySecondMarkFailedOverwritesPhase()
    {
        // This documents the underlying state-machine behavior that the
        // outer-catch guard exists to prevent. If the guard is removed
        // (regressing fix #1), this is the failure mode that surfaces:
        // CurrentPhase becomes "Failed" instead of the actionable
        // "ONNX download failed" the operator needs to see.
        var state = new McpWarmupState();
        state.MarkFailed("ONNX download failed", "first");
        state.MarkFailed(nameof(ScrapeJobStatus.Failed), "second");

        Assert.Equal(nameof(ScrapeJobStatus.Failed), state.CurrentPhase);
    }
}
