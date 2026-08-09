// TesseractRegistration.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Where the user's separately licensed Tesseract install lives. Recorded so the
///     Tray can point a Docling child process at it; never installed by SaddleRAG.
/// </summary>
public sealed record TesseractRegistration(string ExecutableDirectory, string TessdataDirectory);
