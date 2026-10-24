// PageContentSelectionException.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>An explicit single-page content selector could not produce article content.</summary>
public sealed class PageContentSelectionException : InvalidOperationException
{
    public PageContentSelectionException(string selector, string url, string reason, Exception? innerException = null)
        : base($"Content selector '{selector}' could not select article content at '{url}': {reason}", innerException)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentException.ThrowIfNullOrEmpty(reason);
    }
}
