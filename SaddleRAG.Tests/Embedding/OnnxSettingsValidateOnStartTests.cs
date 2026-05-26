// OnnxSettingsValidateOnStartTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

/// <summary>
///     Locks in the wiring that round 2 fix #2 (commit a8f728c)
///     established: <c>AddOptions&lt;OnnxSettings&gt;().Bind(...).ValidateOnStart()</c>
///     fires the validator during <c>IHost.StartAsync</c>, not lazily on
///     first <c>IOptions.Value</c> access. Without the
///     <c>.ValidateOnStart()</c> call the validator runs deep inside the
///     warmup background thread and surfaces as a generic warmup failure;
///     these tests would fail if a future refactor reverted that wiring.
///
///     The Onnx settings own validator tests in OnnxSettingsValidatorTests
///     cover the predicate's logic; these cover the host-startup pipeline
///     wiring around it.
/// </summary>
public sealed class OnnxSettingsValidateOnStartTests
{
    [Fact]
    public async Task ValidateOnStartFailsHostStartWhenOnnxRegistryIsInvalid()
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Configuration.AddInMemoryCollection(smBadConfig);

        builder.Services.AddOptions<OnnxSettings>()
               .Bind(builder.Configuration.GetSection(OnnxSettings.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<OnnxSettings>, OnnxSettingsValidator>();

        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(
            async () => await host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains("EmbeddingModels is empty", ex.Message);
    }

    [Fact]
    public async Task ValidateOnStartAllowsHostStartWhenOnnxRegistryIsValid()
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Configuration.AddInMemoryCollection(smGoodConfig);

        builder.Services.AddOptions<OnnxSettings>()
               .Bind(builder.Configuration.GetSection(OnnxSettings.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<OnnxSettings>, OnnxSettingsValidator>();

        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);

        // Validator passed → IOptions.Value resolves cleanly.
        var options = host.Services.GetRequiredService<IOptions<OnnxSettings>>();
        Assert.NotNull(options.Value);
        Assert.True(options.Value.Enabled);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ValidateOnStartDoesNotFireWhenOnnxDisabled()
    {
        // Onnx.Enabled=false is the default. The validator returns Success
        // without inspecting registries, so a deployment that hasn't opted
        // into ONNX yet should start cleanly even with empty registries.
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                                                        {
                                                            [$"{OnnxSettings.SectionName}:Enabled"] = "false",
                                                            [$"{OnnxSettings.SectionName}:EmbeddingEnabled"] = "false"
                                                        });

        builder.Services.AddOptions<OnnxSettings>()
               .Bind(builder.Configuration.GetSection(OnnxSettings.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<OnnxSettings>, OnnxSettingsValidator>();

        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static readonly Dictionary<string, string?> smBadConfig = new()
    {
        [$"{OnnxSettings.SectionName}:Enabled"] = "true",
        [$"{OnnxSettings.SectionName}:EmbeddingEnabled"] = "true"
        // EmbeddingModels intentionally empty → validator must reject.
    };

    private static readonly Dictionary<string, string?> smGoodConfig = new()
    {
        [$"{OnnxSettings.SectionName}:Enabled"] = "true",
        [$"{OnnxSettings.SectionName}:EmbeddingEnabled"] = "true",
        [$"{OnnxSettings.SectionName}:EmbeddingModels:0:Name"] = "nomic",
        [$"{OnnxSettings.SectionName}:EmbeddingModels:0:RepoId"] = "test/nomic",
        [$"{OnnxSettings.SectionName}:EmbeddingModels:0:ModelFile"] = "model.onnx",
        [$"{OnnxSettings.SectionName}:EmbeddingModels:0:TokenizerFamily"] = "Bert",
        [$"{OnnxSettings.SectionName}:EmbeddingModels:0:VocabFile"] = "vocab.txt"
    };
}
