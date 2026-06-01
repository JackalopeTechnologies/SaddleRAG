// RegistrarResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

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
        var result = new RegistrarResult(results, []);
        return result;
    }

    public static RegistrarResult ForUnregister(IReadOnlyList<UnregisterResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var result = new RegistrarResult([], results);
        return result;
    }
}
