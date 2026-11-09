// WindowsMongoServiceControlTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Database;

namespace SaddleRAG.Tests.Database;

public sealed class WindowsMongoServiceControlTests
{
    [Fact]
    public void GetStateReportsNotInstalledForAServiceThatDoesNotExist()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        var control = new WindowsMongoServiceControl();

        Assert.Equal(MongoServiceState.NotInstalled, control.GetState(MissingServiceName));
    }

    [Fact]
    public void GetStateRejectsABlankServiceName()
    {
        var control = new WindowsMongoServiceControl();

        Assert.Throws<ArgumentException>(() => control.GetState(" "));
    }

    private const string MissingServiceName = "SaddleRagNoSuchService_7f3a";

    private const string WindowsOnlySkipReason =
        "Querying a Windows service requires Windows.";
}
