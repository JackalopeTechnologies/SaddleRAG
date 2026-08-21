// PatchAppSettingsLoggingTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json.Nodes;

namespace SaddleRAG.Tests.Installer;

/// <summary>
///     An upgrade preserves the installed appsettings.json, so a Logging:LogLevel
///     entry added to the shipped template (e.g. the System.Net.Http.HttpClient
///     HTTP-spam suppression) only reaches an existing box when the patch script
///     seeds it. These lock that convergence — seed the shipped defaults when a
///     key is absent, preserve any deliberately-set value — mirroring the
///     Docling-timeout coverage.
/// </summary>
[Collection(PowerShellScriptCollection.Name)]
public sealed class PatchAppSettingsLoggingTests
{
    [Fact]
    public void MissingHttpClientLogLevelIsSeededOnUpgrade()
    {
        JsonObject settings = Settings();
        settings["Logging"] = new JsonObject
                                  {
                                      ["LogLevel"] = new JsonObject { ["Default"] = "Information" }
                                  };

        JsonObject patched = PatchAppSettingsTestDriver.Run(settings, doclingEndpoint: string.Empty);

        JsonObject logLevel = patched["Logging"]!["LogLevel"]!.AsObject();
        Assert.Equal("Warning", logLevel["System.Net.Http.HttpClient"]!.GetValue<string>());
        Assert.Equal("Information", logLevel["Default"]!.GetValue<string>());
    }

    [Fact]
    public void DeliberateHttpClientLogLevelSurvivesUpgrade()
    {
        JsonObject settings = Settings();
        settings["Logging"] = new JsonObject
                                  {
                                      ["LogLevel"] = new JsonObject
                                                         {
                                                             ["System.Net.Http.HttpClient"] = "Information"
                                                         }
                                  };

        JsonObject patched = PatchAppSettingsTestDriver.Run(settings, doclingEndpoint: string.Empty);

        Assert.Equal("Information",
                     patched["Logging"]!["LogLevel"]!["System.Net.Http.HttpClient"]!.GetValue<string>());
    }

    [Fact]
    public void AbsentLoggingBlockIsCreatedWithShippedDefaults()
    {
        JsonObject settings = Settings();

        JsonObject patched = PatchAppSettingsTestDriver.Run(settings, doclingEndpoint: string.Empty);

        JsonObject logLevel = patched["Logging"]!["LogLevel"]!.AsObject();
        Assert.Equal("Information", logLevel["Default"]!.GetValue<string>());
        Assert.Equal("Warning", logLevel["Microsoft.AspNetCore"]!.GetValue<string>());
        Assert.Equal("Warning", logLevel["System.Net.Http.HttpClient"]!.GetValue<string>());
    }

    [Fact]
    public void UnrelatedLogLevelEntriesArePreservedWhileSeeding()
    {
        JsonObject settings = Settings();
        settings["Logging"] = new JsonObject
                                  {
                                      ["LogLevel"] = new JsonObject { ["SaddleRAG.Ingestion"] = "Debug" }
                                  };

        JsonObject patched = PatchAppSettingsTestDriver.Run(settings, doclingEndpoint: string.Empty);

        JsonObject logLevel = patched["Logging"]!["LogLevel"]!.AsObject();
        Assert.Equal("Debug", logLevel["SaddleRAG.Ingestion"]!.GetValue<string>());
        Assert.Equal("Warning", logLevel["System.Net.Http.HttpClient"]!.GetValue<string>());
    }

    private static JsonObject Settings() => new()
        {
            ["MongoDB"] = new JsonObject
                              {
                                  ["Profiles"] = new JsonObject
                                                     {
                                                         ["local"] = new JsonObject
                                                                         {
                                                                             ["ConnectionString"] = "mongodb://localhost:27017",
                                                                             ["DatabaseName"] = "SaddleRAG"
                                                                         }
                                                     }
                              },
            ["Ollama"] = new JsonObject { ["Endpoint"] = "http://localhost:11434" },
            ["Onnx"] = new JsonObject { ["ExecutionProvider"] = "Cpu" }
        };
}
