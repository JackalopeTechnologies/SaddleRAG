// TestPaths.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tests.ClientIntegration;

internal static class TestPaths
{
    public static string FixtureDir(string client, string scenario)
        => Path.Combine(AppContext.BaseDirectory, "ClientIntegration", "Fixtures", client, scenario);

    public static string FixtureFile(string client, string scenario, string fileName)
        => Path.Combine(FixtureDir(client, scenario), fileName);
}
