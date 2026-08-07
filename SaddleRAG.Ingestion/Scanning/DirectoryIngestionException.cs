// DirectoryIngestionException.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>A directory processing failure with a stable actionable reason code.</summary>
public sealed class DirectoryIngestionException : Exception
{
    public DirectoryIngestionException(string reasonCode, string detail, string? relativePath = null)
        : base(detail)
    {
        ArgumentException.ThrowIfNullOrEmpty(reasonCode);
        ArgumentException.ThrowIfNullOrEmpty(detail);
        if (relativePath != null)
            ArgumentException.ThrowIfNullOrEmpty(relativePath);
        ReasonCode = reasonCode;
        Detail = detail;
        RelativePath = relativePath;
    }

    public string ReasonCode { get; }

    public string Detail { get; }

    public string? RelativePath { get; }
}
