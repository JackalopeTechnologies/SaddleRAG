// ComponentDefinition.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Where one component lives and whether it runs on this computer, as read from the installed
///     settings. Nothing here comes from a running service, so it is known while the service is down.
///     A remote MongoDB row carries no endpoint and no port, and no row ever carries credentials,
///     query strings, or the host of a remote connection string.
/// </summary>
/// <param name="Kind">Which component this row describes.</param>
/// <param name="DisplayName">The name shown to the operator.</param>
/// <param name="Endpoint">The address the component listens on, or null when it is not configured or is a remote MongoDB.</param>
/// <param name="Port">The TCP port, or null when it is not configured or is a remote MongoDB.</param>
/// <param name="HostedLocally">True only when the component runs on this computer (loopback).</param>
/// <param name="WindowsServiceName">The Windows service that runs the component, or null when none is known.</param>
/// <param name="ExecutablePath">The configured executable, when the settings name one (Ollama).</param>
/// <param name="ModelsDirectory">The configured model folder, when the settings name one (Ollama).</param>
public sealed record ComponentDefinition(ComponentKind Kind,
                                         string DisplayName,
                                         Uri? Endpoint,
                                         int? Port,
                                         bool HostedLocally,
                                         string? WindowsServiceName,
                                         string? ExecutablePath,
                                         string? ModelsDirectory);
