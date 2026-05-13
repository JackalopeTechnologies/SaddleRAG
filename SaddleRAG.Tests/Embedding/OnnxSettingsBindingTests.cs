// OnnxSettingsBindingTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Configuration;
using SaddleRAG.Core.Enums;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

/// <summary>
///     Covers the IConfiguration string → OnnxSettings.ExecutionProvider
///     enum binding path. appsettings.json stores the value as a string
///     ("Cpu" / "DirectMl" / "Cuda"); production relies on the
///     ConfigurationBinder's default behavior to convert it to the
///     OnnxExecutionProvider enum. Tests in OnnxSettingsTests exercise
///     Enum.TryParse directly but never the binder, leaving the
///     load-bearing path uncovered. A regression that renames the enum
///     members, adds [JsonConverter] attributes, or changes the binder's
///     enum handling would silently fall back to the enum's default
///     value (Cpu) without these tests catching it.
/// </summary>
public sealed class OnnxSettingsBindingTests
{
    [Theory]
    [InlineData("Cpu", OnnxExecutionProvider.Cpu)]
    [InlineData("DirectMl", OnnxExecutionProvider.DirectMl)]
    [InlineData("directml", OnnxExecutionProvider.DirectMl)]
    [InlineData("DIRECTML", OnnxExecutionProvider.DirectMl)]
    [InlineData("Cuda", OnnxExecutionProvider.Cuda)]
    [InlineData("cuda", OnnxExecutionProvider.Cuda)]
    public void ExecutionProviderBindsCaseInsensitivelyFromConfigurationString(string configValue,
                                                                                OnnxExecutionProvider expected)
    {
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                                                       {
                                                           [ConfigKey] = configValue
                                                       })
                            .Build();

        var settings = configuration.GetSection(OnnxSettings.SectionName).Get<OnnxSettings>();

        Assert.NotNull(settings);
        Assert.Equal(expected, settings.ExecutionProvider);
    }

    [Fact]
    public void ExecutionProviderDefaultsToCpuWhenConfigKeyMissing()
    {
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>())
                            .Build();

        var settings = configuration.GetSection(OnnxSettings.SectionName).Get<OnnxSettings>()
                       ?? new OnnxSettings();

        Assert.Equal(OnnxExecutionProvider.Cpu, settings.ExecutionProvider);
    }

    [Fact]
    public void ExecutionProviderUnknownValueIsRejectedAtBindTime()
    {
        // The ConfigurationBinder throws InvalidOperationException when a
        // string can't be converted to the target enum. Verifies the bind
        // layer does its job — the binder rejecting "OpenVino" is what
        // protects OnnxSettings.ExecutionProvider from holding a value
        // outside the enum.
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                                                       {
                                                           [ConfigKey] = "OpenVino"
                                                       })
                            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            configuration.GetSection(OnnxSettings.SectionName).Get<OnnxSettings>()
        );
    }

    private const string ConfigKey = "Onnx:ExecutionProvider";

    [Theory]
    [InlineData("Disable", OnnxGraphOptimizationLevel.Disable)]
    [InlineData("Basic", OnnxGraphOptimizationLevel.Basic)]
    [InlineData("basic", OnnxGraphOptimizationLevel.Basic)]
    [InlineData("BASIC", OnnxGraphOptimizationLevel.Basic)]
    [InlineData("Extended", OnnxGraphOptimizationLevel.Extended)]
    [InlineData("All", OnnxGraphOptimizationLevel.All)]
    public void GraphOptimizationLevelBindsCaseInsensitivelyFromConfigurationString(
        string configValue, OnnxGraphOptimizationLevel expected)
    {
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                                                       {
                                                           [GraphOptimizationLevelKey] = configValue
                                                       })
                            .Build();

        var settings = configuration.GetSection(OnnxSettings.SectionName).Get<OnnxSettings>();

        Assert.NotNull(settings);
        Assert.Equal(expected, settings.GraphOptimizationLevel);
    }

    [Fact]
    public void GraphOptimizationLevelDefaultsToBasicWhenConfigKeyMissing()
    {
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>())
                            .Build();

        var settings = configuration.GetSection(OnnxSettings.SectionName).Get<OnnxSettings>()
                       ?? new OnnxSettings();

        Assert.Equal(OnnxGraphOptimizationLevel.Basic, settings.GraphOptimizationLevel);
    }

    [Fact]
    public void GraphOptimizationLevelUnknownValueIsRejectedAtBindTime()
    {
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                                                       {
                                                           [GraphOptimizationLevelKey] = "Aggressive"
                                                       })
                            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            configuration.GetSection(OnnxSettings.SectionName).Get<OnnxSettings>()
        );
    }

    private const string GraphOptimizationLevelKey = "Onnx:GraphOptimizationLevel";
}
