// OnnxDeviceLossTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class OnnxDeviceLossTests
{
    [Theory]
    [InlineData("887A0005")]
    [InlineData("887A0006")]
    [InlineData("887A0007")]
    [InlineData("887A0020")]
    public void DetectsAllDxgiDeviceLossCodes(string code)
    {
        string message = "[ErrorCode:Fail] ...DmlExecutionProvider\\src\\ExecutionProvider.cpp(952)... " +
                         $"Exception(14) tid(171c) {code} The GPU device instance has been suspended.";

        Assert.True(OnnxDeviceLoss.IsDeviceLossMessage(message));
    }

    [Fact]
    public void MatchesCaseInsensitively()
    {
        Assert.True(OnnxDeviceLoss.IsDeviceLossMessage("failure 887a0005 lowercase"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[ErrorCode:Fail] model file is corrupt")]
    [InlineData("some unrelated ORT failure")]
    public void IgnoresNonDeviceLossMessages(string? message)
    {
        Assert.False(OnnxDeviceLoss.IsDeviceLossMessage(message));
    }
}
