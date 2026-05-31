// ClientResultFormatterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Models;
using Xunit;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class ClientResultFormatterTests
{
    private const string ConfigPath = "C:/cfg.json";

    [Fact]
    public void SummarizeRegisterMarksOkErrAndSkipDistinctly()
    {
        RegistrarResult result = RegistrarResult.ForRegister(
        [
            RegisterResult.Ok("claude-code", ConfigPath, "wrote"),
            RegisterResult.Failed("codex", ConfigPath, "boom"),
            RegisterResult.SkippedFor("windsurf", "agent not detected")
        ]);

        string summary = ClientResultFormatter.SummarizeRegister(result);

        Assert.Contains("OK  claude-code: wrote", summary);
        Assert.Contains("ERR codex: boom", summary);
        Assert.Contains("SKIP windsurf: agent not detected", summary);
    }

    [Fact]
    public void SummarizeUnregisterMarksOkNoopAndErr()
    {
        RegistrarResult result = RegistrarResult.ForUnregister(
        [
            UnregisterResult.Removed("claude-code", ConfigPath, "removed"),
            UnregisterResult.NoOp("codex", ConfigPath, "nothing to do"),
            UnregisterResult.Failed("cursor", ConfigPath, "boom")
        ]);

        string summary = ClientResultFormatter.SummarizeUnregister(result);

        Assert.Contains("OK  claude-code: removed", summary);
        Assert.Contains("NOOP codex: nothing to do", summary);
        Assert.Contains("ERR cursor: boom", summary);
    }

    [Fact]
    public void SummarizeStatusReportsTwoAxisCombinations()
    {
        StatusResult registered = new("claude-code", ConfigPath, true, true, true, true, "ok");
        StatusResult missing = new("codex", ConfigPath, false, false, false, null, "absent");
        StatusResult oldEndpoint = new("cursor", ConfigPath, true, true, false, null, "stale");
        StatusResult installedMissing = new("windsurf", ConfigPath, true, false, false, null, "no entry");
        List<ClientResultFormatter.StatusLineInput> inputs =
        [
            new ClientResultFormatter.StatusLineInput("Claude Code", registered, true),
            new ClientResultFormatter.StatusLineInput("Codex", missing, false),
            new ClientResultFormatter.StatusLineInput("Cursor", oldEndpoint, true),
            new ClientResultFormatter.StatusLineInput("Windsurf", installedMissing, true)
        ];

        string summary = ClientResultFormatter.SummarizeStatus(inputs);

        Assert.Contains("Claude Code: installed, registered", summary);
        Assert.Contains("Codex: absent, not registered", summary);
        Assert.Contains("Cursor: installed, old endpoint", summary);
        Assert.Contains("Windsurf: installed, not registered", summary);
    }
}
