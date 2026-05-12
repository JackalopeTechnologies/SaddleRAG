// RegistrarResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.ClientIntegration.Models;

public sealed record RegistrarResult(
    IReadOnlyList<RegisterResult> RegisterResults,
    IReadOnlyList<UnregisterResult> UnregisterResults)
{
    public bool AllRegisterSucceeded => RegisterResults.All(r => r.Success);
    public bool AllUnregisterSucceeded => UnregisterResults.All(r => r.Success);

    public static RegistrarResult ForRegister(IReadOnlyList<RegisterResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var result = new RegistrarResult(results, Array.Empty<UnregisterResult>());
        return result;
    }

    public static RegistrarResult ForUnregister(IReadOnlyList<UnregisterResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var result = new RegistrarResult(Array.Empty<RegisterResult>(), results);
        return result;
    }
}
