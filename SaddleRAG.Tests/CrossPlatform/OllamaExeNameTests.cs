// OllamaExeNameTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial

namespace SaddleRAG.Tests.CrossPlatform;

public class OllamaExeNameTests
{
    [Fact]
    public void ReturnsPlatformSpecificExeName()
    {
        string expected = OperatingSystem.IsWindows() ? "ollama.exe" : "ollama";
        Assert.Equal(expected, SaddleRAG.Ingestion.Embedding.OllamaBootstrapper.OllamaExeName);
    }
}
