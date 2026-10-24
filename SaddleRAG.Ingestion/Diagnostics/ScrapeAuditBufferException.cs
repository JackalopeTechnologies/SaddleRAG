// ScrapeAuditBufferException.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Diagnostics;

/// <summary>
///     Audit admission failed because the bounded buffer is full or stopping.
///     Ingestion must stop rather than continue without recording its outcomes.
/// </summary>
public sealed class ScrapeAuditBufferException : InvalidOperationException
{
    public ScrapeAuditBufferException() : base(BufferUnavailableMessage)
    {
    }

    private const string BufferUnavailableMessage =
        "Scrape audit buffering is full or stopping; ingestion cannot continue without recording audit events.";
}
