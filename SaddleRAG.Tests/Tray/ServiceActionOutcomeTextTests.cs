// ServiceActionOutcomeTextTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

public sealed class ServiceActionOutcomeTextTests
{
    private const string Subject = "SaddleRAG MCP service";

    public static TheoryData<ServiceActionOutcome> AllOutcomes()
    {
        TheoryData<ServiceActionOutcome> data = [];
        foreach (ServiceActionOutcome outcome in Enum.GetValues<ServiceActionOutcome>())
            data.Add(outcome);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void EveryOutcomeHasPlainText(ServiceActionOutcome outcome)
    {
        string text = ServiceActionOutcomeText.Describe(outcome, ServiceAction.Restart, Subject);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("{", text);
    }

    [Fact]
    public void CancelledSaysWindowsDidNotGetApproval()
    {
        string text = ServiceActionOutcomeText.Describe(ServiceActionOutcome.Cancelled, ServiceAction.Start, Subject);

        Assert.Equal("Cancelled — Windows did not get administrator approval.", text);
    }

    [Fact]
    public void UnsafeInstallLocationTellsTheOperatorToReinstall()
    {
        string text = ServiceActionOutcomeText.Describe(ServiceActionOutcome.UnsafeInstallLocation,
                                                        ServiceAction.Start,
                                                        Subject);

        Assert.Equal("Refused: the SaddleRAG install folder can be changed by non-administrators. "
                     + "Reinstall SaddleRAG to a protected folder such as C:\\Program Files.",
                     text);
    }

    [Fact]
    public void FailedNamesBothTheEventLogAndTheTrayLog()
    {
        // The script records its own failures in the event log; a failure to run it at all is only
        // in the tray log, so the operator is pointed at both.
        string text = ServiceActionOutcomeText.Describe(ServiceActionOutcome.Failed, ServiceAction.Restart, Subject);

        Assert.Contains("Windows Application event log (source SaddleRAG Service Actions)", text, StringComparison.Ordinal);
        Assert.Contains(@"%LOCALAPPDATA%\SaddleRAG\tray.log", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ServiceAction.Start, "SaddleRAG MCP service started.")]
    [InlineData(ServiceAction.Stop, "SaddleRAG MCP service stopped.")]
    [InlineData(ServiceAction.Restart, "SaddleRAG MCP service restarted.")]
    public void SuccessNamesTheSubjectAndWhatHappened(ServiceAction action, string expected)
    {
        string text = ServiceActionOutcomeText.Describe(ServiceActionOutcome.Succeeded, action, Subject);

        Assert.Equal(expected, text);
    }
}
