// ExternalToolRegistration.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     The whole external-tool registry document. Either entry may be absent, which
///     means "not registered" — the launcher reports that rather than guessing a path.
/// </summary>
public sealed record ExternalToolRegistration(DoclingRegistration? Docling, TesseractRegistration? Tesseract)
{
    /// <summary>Nothing registered; also what an unreadable registry file reads back as.</summary>
    public static ExternalToolRegistration Empty { get; } = new(Docling: null, Tesseract: null);
}
