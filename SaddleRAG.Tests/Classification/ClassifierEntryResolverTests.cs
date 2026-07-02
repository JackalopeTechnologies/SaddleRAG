// ClassifierEntryResolverTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Runtime.CompilerServices;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Classification;

/// <summary>
///     Locks in <see cref="ClassifierEntryResolver" />'s pure EP-to-entry
///     resolution: the runtime execution provider selects the matching
///     classifier variant (directml/cuda/cpu), an explicit
///     <c>ActiveClassifierModel</c> override wins over auto-resolution, and a
///     missing EP-matching entry falls back to the CPU entry (then first).
/// </summary>
public sealed class ClassifierEntryResolverTests
{
    [Fact]
    public void DirectMlResolvesToDirectMlEntry()
    {
        var settings = BuildDefaultRegistrySettings();

        var entry = ClassifierEntryResolver.Resolve(settings, OnnxExecutionProvider.DirectMl);

        Assert.Equal(OnnxSettings.Phi3MiniDirectMlName, entry.Name);
    }

    [Fact]
    public void CudaResolvesToCudaEntry()
    {
        var settings = BuildDefaultRegistrySettings();

        var entry = ClassifierEntryResolver.Resolve(settings, OnnxExecutionProvider.Cuda);

        Assert.Equal(OnnxSettings.Phi3MiniCudaName, entry.Name);
    }

    [Fact]
    public void CpuResolvesToCpuEntry()
    {
        var settings = BuildDefaultRegistrySettings();

        var entry = ClassifierEntryResolver.Resolve(settings, OnnxExecutionProvider.Cpu);

        Assert.Equal(OnnxSettings.Phi3MiniCpuName, entry.Name);
    }

    [Fact]
    public void ExplicitActiveOverrideWinsOverExecutionProvider()
    {
        var settings = BuildDefaultRegistrySettings();
        settings.ActiveClassifierModel = OnnxSettings.Phi3MiniCpuName;

        var entry = ClassifierEntryResolver.Resolve(settings, OnnxExecutionProvider.DirectMl);

        Assert.Equal(OnnxSettings.Phi3MiniCpuName, entry.Name);
    }

    [Fact]
    public void MissingEpEntryFallsBackToCpuEntry()
    {
        var settings = new OnnxSettings
        {
            ClassifierModels =
            [
                new ClassifierModelEntry
                {
                    Name = OnnxSettings.Phi3MiniCpuName,
                    RepoId = OnnxSettings.Phi3MiniRepoId,
                    ModelFolder = OnnxSettings.Phi3MiniCpuFolder
                }
            ]
        };

        var entry = ClassifierEntryResolver.Resolve(settings, OnnxExecutionProvider.DirectMl);

        Assert.Equal(OnnxSettings.Phi3MiniCpuName, entry.Name);
    }

    [Fact]
    public void MissingEpAndCpuEntryFallsBackToFirstEntry()
    {
        var settings = new OnnxSettings
        {
            ClassifierModels =
            [
                new ClassifierModelEntry
                {
                    Name = "custom-only",
                    RepoId = OnnxSettings.Phi3MiniRepoId,
                    ModelFolder = OnnxSettings.Phi3MiniDirectMlFolder
                }
            ]
        };

        var entry = ClassifierEntryResolver.Resolve(settings, OnnxExecutionProvider.Cuda);

        Assert.Equal("custom-only", entry.Name);
    }

    [Fact]
    public void EmptyRegistryThrows()
    {
        var settings = new OnnxSettings { ClassifierModels = [] };

        Assert.Throws<InvalidOperationException>(
            () => ClassifierEntryResolver.Resolve(settings, OnnxExecutionProvider.Cpu)
        );
    }

    [Fact]
    public void UnknownExplicitActiveOverrideThrows()
    {
        var settings = BuildDefaultRegistrySettings();
        settings.ActiveClassifierModel = "does-not-exist";

        Assert.Throws<InvalidOperationException>(
            () => ClassifierEntryResolver.Resolve(settings, OnnxExecutionProvider.Cpu)
        );
    }

    [Fact]
    public void RequestedDirectMlNotCompiledInClampsToCpuEntry()
    {
        // Regression for issue #135: runtime-overrides.json requested DirectMl
        // on a CPU-only build; resolving the directml model variant against the
        // CPU GenAI native access-violated in OgaCreateGenerator and killed the
        // service. The requested EP must clamp to the compiled-in set BEFORE
        // variant selection.
        var settings = BuildDefaultRegistrySettings();

        var entry = ClassifierEntryResolver.Resolve(settings,
                                                    OnnxExecutionProvider.DirectMl,
                                                    [OnnxExecutionProvider.Cpu]);

        Assert.Equal(OnnxSettings.Phi3MiniCpuName, entry.Name);
    }

    [Fact]
    public void RequestedDirectMlCompiledInResolvesDirectMlEntry()
    {
        var settings = BuildDefaultRegistrySettings();

        var entry = ClassifierEntryResolver.Resolve(settings,
                                                    OnnxExecutionProvider.DirectMl,
                                                    [OnnxExecutionProvider.Cpu, OnnxExecutionProvider.DirectMl]);

        Assert.Equal(OnnxSettings.Phi3MiniDirectMlName, entry.Name);
    }

    [Fact]
    public void RequestedCudaNotCompiledInClampsToCpuEntry()
    {
        var settings = BuildDefaultRegistrySettings();

        var entry = ClassifierEntryResolver.Resolve(settings,
                                                    OnnxExecutionProvider.Cuda,
                                                    [OnnxExecutionProvider.Cpu]);

        Assert.Equal(OnnxSettings.Phi3MiniCpuName, entry.Name);
    }

    [Fact]
    public void ExplicitActiveOverrideStillWinsOverClamp()
    {
        var settings = BuildDefaultRegistrySettings();
        settings.ActiveClassifierModel = OnnxSettings.Phi3MiniDirectMlName;

        var entry = ClassifierEntryResolver.Resolve(settings,
                                                    OnnxExecutionProvider.DirectMl,
                                                    [OnnxExecutionProvider.Cpu]);

        Assert.Equal(OnnxSettings.Phi3MiniDirectMlName, entry.Name);
    }

    [Fact]
    public void NullCompiledInProvidersThrows()
    {
        var settings = BuildDefaultRegistrySettings();

        Assert.Throws<ArgumentNullException>(
            () => ClassifierEntryResolver.Resolve(settings,
                                                  OnnxExecutionProvider.Cpu,
                                                  NullRef<IReadOnlyList<OnnxExecutionProvider>>())
        );
    }

    private static OnnxSettings BuildDefaultRegistrySettings()
    {
        // The OnnxSettings default ClassifierModels list already carries the
        // three verified Phi-3-mini variants (directml, cuda, cpu), so a fresh
        // instance is the registry under test.
        return new OnnxSettings();
    }

    private static T NullRef<T>() where T : class
    {
        T? nullable = null;
        return Unsafe.As<T?, T>(ref nullable);
    }
}
