// McpWarmupClassifierWarmDecisionTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Mcp;

#endregion

namespace SaddleRAG.Tests.Mcp;

/// <summary>
///     Locks in the contract that warmup's completed-vs-degraded decision is
///     driven by the <em>active</em> classifier backend. On the default
///     all-ONNX path the ONNX classifier warm alone decides the status and the
///     Ollama generate warm is never consulted — the regression that let a
///     stale, unconditional Ollama phi warm both pin the model in VRAM and
///     drive the completion status even though nothing used it.
/// </summary>
public sealed class McpWarmupClassifierWarmDecisionTests
{
    [Theory]
    // All-ONNX path (ollamaClassifierActive = false): only the ONNX warm counts.
    // ONNX warmed → Completed.
    [InlineData(false, true, false, true)]
    // ONNX warm failed → degraded.
    [InlineData(false, false, false, false)]
    // ONNX warmed; the Ollama outcome is ignored.
    [InlineData(false, true, true, true)]
    // Regression guard: a "successful" Ollama warm must NOT rescue the status
    // when the active classifier is ONNX and its warm failed.
    [InlineData(false, false, true, false)]
    // Ollama classifier active: only the Ollama warm counts.
    // Ollama warmed → Completed.
    [InlineData(true, false, true, true)]
    // Ollama warm failed → degraded, whatever the ONNX warm did.
    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, false)]
    public void IsActiveClassifierWarmReflectsActiveBackend(bool ollamaClassifierActive,
                                                            bool onnxClassifierWarm,
                                                            bool ollamaWarmSucceeded,
                                                            bool expected)
    {
        bool actual = McpWarmupService.IsActiveClassifierWarm(ollamaClassifierActive,
                                                              onnxClassifierWarm,
                                                              ollamaWarmSucceeded
                                                             );

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AllOnnxPathIgnoresOllamaWarmOutcome()
    {
        // When ONNX is the active classifier its warm decides the status;
        // the Ollama generate warm outcome (true here) is irrelevant.
        bool warmWhenOnnxSucceeded = McpWarmupService.IsActiveClassifierWarm(ollamaClassifierActive: false,
                                                                             onnxClassifierWarm: true,
                                                                             ollamaWarmSucceeded: false
                                                                            );
        bool degradedWhenOnnxFailed = McpWarmupService.IsActiveClassifierWarm(ollamaClassifierActive: false,
                                                                              onnxClassifierWarm: false,
                                                                              ollamaWarmSucceeded: true
                                                                             );

        Assert.True(warmWhenOnnxSucceeded);
        Assert.False(degradedWhenOnnxFailed);
    }
}
