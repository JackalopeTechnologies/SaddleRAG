// IOperatorConfirmation.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Asks the operator before the tray stops a process. The tray never stops Docling or Ollama
///     without this answer, because the process it found on the port may not be the one expected.
/// </summary>
public interface IOperatorConfirmation
{
    /// <summary>Returns true only when the operator agrees to stop <paramref name="process" />.</summary>
    /// <param name="component">The component the operator asked to stop or restart.</param>
    /// <param name="process">The process found listening on the component's port.</param>
    /// <param name="looksExpected">
    ///     True when the process name is one that component normally runs as; false means some other
    ///     program holds the port, and the prompt should say so.
    /// </param>
    Task<bool> ConfirmStopAsync(ComponentDefinition component, ListeningProcess process, bool looksExpected);
}
