// FailureType.cs

// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Classification of a page fetch failure for retry logic.
/// </summary>
public enum FailureType

{
    Transient,

    Permanent,

    AuthenticationRequired,

    Timeout,

    ContentExtractionFailed
}
