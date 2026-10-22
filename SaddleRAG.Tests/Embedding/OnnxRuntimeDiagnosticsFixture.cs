// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Tests.Embedding;

[assembly: AssemblyFixture(typeof(OnnxRuntimeDiagnosticsFixture))]

namespace SaddleRAG.Tests.Embedding;

/// <summary>
/// Creates the process-wide native logging environment before tests construct
/// raw SessionOptions. Production model loaders perform this same initialization
/// lazily; the fixture preserves that order for tests that bypass the loaders.
/// </summary>
public sealed class OnnxRuntimeDiagnosticsFixture
{
    public OnnxRuntimeDiagnosticsFixture()
    {
        Assert.True(OnnxRuntimeDiagnostics.EnsureInitialized());
    }
}
