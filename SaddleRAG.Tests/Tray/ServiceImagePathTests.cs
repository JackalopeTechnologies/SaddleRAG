// ServiceImagePathTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

/// <summary>
///     Mirrors the -TestImagePath cases of Restart-SaddleRAGComponent.ps1: the tray must offer exactly
///     the services the elevated script would accept.
/// </summary>
public sealed class ServiceImagePathTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\MongoDB\\Server\\8.0\\bin\\mongod.exe\" --config \"C:\\cfg\\mongod.cfg\" --service",
                "mongod.exe")]
    [InlineData("C:\\MongoDB\\bin\\mongod.exe --config C:\\cfg\\mongod.cfg --service", "mongod.exe")]
    [InlineData("C:\\MongoDB\\bin\\mongod.exe", "mongod.exe")]
    [InlineData("  \tC:\\MongoDB\\bin\\MONGOD.EXE", "MONGOD.EXE")]
    [InlineData("C:\\Windows\\tool -log C:\\data\\mongod.exe", "tool")]
    [InlineData("C:\\Windows\\tool.com -x C:/d/mongod.exe", "tool.com")]
    [InlineData("\"C:\\evil\\mongod.exe.bat\"", "mongod.exe.bat")]
    [InlineData("C:\\Program Files\\MongoDB\\bin\\mongod.exe", "Program")]
    [InlineData("C:/MongoDB/bin/mongod.exe", "mongod.exe")]
    [InlineData("%windir%\\system32\\svchost.exe -k netsvcs -p", "svchost.exe")]
    public void ExtractsTheExecutableFileNameTheSameWayTheElevatedScriptDoes(string imagePath, string expected)
    {
        Assert.Equal(expected, ServiceImagePath.ExecutableFileName(imagePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData("C:\\MongoDB\\bin\\mongod.exe\u00A0--service")]
    [InlineData("\"C:\\x.exe\"mongod.exe")]
    [InlineData("\"C:\\unterminated\\mongod.exe")]
    [InlineData("\"C:\\bad|name\\mongod.exe\"")]
    [InlineData("\"C:\\bad<dir>\\mongod.exe\"")]
    public void ReturnsNullWhenTheImagePathCannotBeParsedUnambiguously(string? imagePath)
    {
        Assert.Null(ServiceImagePath.ExecutableFileName(imagePath));
    }

    [Fact]
    public void ServicesRunningMatchesByFileNameIgnoringCaseAndSortsByOrdinalServiceName()
    {
        KeyValuePair<string, string?>[] services =
        [
            new("zMongo", "\"C:\\a\\mongod.exe\" --service"),
            new("Other", "C:\\Windows\\svchost.exe -k netsvcs"),
            new("MongoDB", "C:\\b\\MONGOD.EXE --service"),
            new("Broken", null),
            new("Tricky", "C:\\Windows\\tool -log C:\\data\\mongod.exe")
        ];

        IReadOnlyList<string> matches = ServiceImagePath.ServicesRunning(services, "mongod.exe");

        Assert.Equal(["MongoDB", "zMongo"], matches);
    }

    [Theory]
    [InlineData("MongoDB", true)]
    [InlineData("Mongo_DB-8.0", true)]
    [InlineData("Mongo DB", false)]
    [InlineData("Mongo\\DB", false)]
    [InlineData("Mongo*", false)]
    [InlineData("MongoDB\n", false)]
    [InlineData("", false)]
    public void OnlyServiceNamesTheScriptAcceptsAreEligible(string serviceName, bool eligible)
    {
        KeyValuePair<string, string?>[] services = [new(serviceName, "C:\\b\\mongod.exe")];

        IReadOnlyList<string> matches = ServiceImagePath.ServicesRunning(services, "mongod.exe");

        Assert.Equal(eligible, matches.Count == 1);
    }
}
