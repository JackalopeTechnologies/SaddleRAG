// StopConfirmationTextTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

public sealed class StopConfirmationTextTests
{
    private static readonly ComponentDefinition smOllama =
        new(ComponentKind.Ollama, "Ollama", new Uri("http://localhost:11434"), Port: 11434,
            HostedLocally: true, WindowsServiceName: null, ExecutablePath: null, ModelsDirectory: null);

    private static readonly string smOllamaPath = Path.Combine(Path.GetTempPath(), "ollama", "ollama.exe");

    private static ListeningProcess Process(string? path) =>
        new(ProcessId: 4321, "ollama", path, new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void TheTitleNamesTheComponent()
    {
        Assert.Equal("Stop Ollama?", StopConfirmationText.Title(smOllama));
    }

    [Fact]
    public void ThePromptNamesTheComponentTheProcessItsIdAndItsProgram()
    {
        string text = StopConfirmationText.Message(smOllama, Process(smOllamaPath), looksExpected: true);

        Assert.Contains("Ollama", text, StringComparison.Ordinal);
        Assert.Contains("Process: ollama", text, StringComparison.Ordinal);
        Assert.Contains("Process ID: 4321", text, StringComparison.Ordinal);
        Assert.Contains($"Program: {smOllamaPath}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExpectedProcessIsStoppedWithTheProcessesItStarted()
    {
        string text = StopConfirmationText.Message(smOllama, Process(smOllamaPath), looksExpected: true);

        Assert.Contains("This process and the processes it started will be stopped.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("does not look like", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnexpectedProcessIsWarnedAboutAndStoppedAlone()
    {
        string text = StopConfirmationText.Message(smOllama, Process(smOllamaPath), looksExpected: false);

        Assert.Contains("This process does not look like Ollama.", text, StringComparison.Ordinal);
        Assert.Contains("Only this process will be stopped, not its child processes.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("the processes it started", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingProgramPathIsSaidPlainly()
    {
        string text = StopConfirmationText.Message(smOllama, Process(path: null), looksExpected: true);

        Assert.Contains("Program: path unavailable", text, StringComparison.Ordinal);
    }
}
