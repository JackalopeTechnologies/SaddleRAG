// OllamaSharedInstallationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Ingestion.Embedding;

namespace SaddleRAG.Tests.Embedding;

public sealed class OllamaSharedInstallationTests
{
    [Fact]
    public void ExplicitInstallationAndModelStoreOverrideServiceAccountDefaults()
    {
        string executable = typeof(OllamaSharedInstallationTests).Assembly.Location;
        string directory = AppContext.BaseDirectory;
        var settings = new OllamaSettings { ExecutablePath = executable, ModelsDirectory = directory };
        var bootstrapper = new OllamaBootstrapper(Options.Create(settings), NullLogger<OllamaBootstrapper>.Instance);
        var start = new ProcessStartInfo();
        bootstrapper.ConfigureModelDirectory(start);
        Assert.Equal(executable, bootstrapper.FindOllamaExecutable());
        Assert.Equal(directory, start.Environment["OLLAMA_MODELS"]);
        Assert.False(settings.AllowAutomaticInstallation);
    }

    [Fact]
    public void MissingSelectedInstallationDoesNotFallBackToAnotherCopy()
    {
        var settings = new OllamaSettings { ExecutablePath = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".exe") };
        var bootstrapper = new OllamaBootstrapper(Options.Create(settings), NullLogger<OllamaBootstrapper>.Instance);
        Assert.Throws<InvalidOperationException>(() => bootstrapper.FindOllamaExecutable());
    }
}
