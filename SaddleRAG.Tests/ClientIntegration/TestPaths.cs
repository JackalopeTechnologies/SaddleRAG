// TestPaths.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Tests.ClientIntegration;

internal static class TestPaths
{
    public static string FixtureDir(string client, string scenario)
        => Path.Combine(AppContext.BaseDirectory, "ClientIntegration", "Fixtures", client, scenario);

    public static string FixtureFile(string client, string scenario, string fileName)
        => Path.Combine(FixtureDir(client, scenario), fileName);
}
