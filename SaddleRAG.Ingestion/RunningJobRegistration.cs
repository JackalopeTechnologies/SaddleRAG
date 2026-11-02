// RunningJobRegistration.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion;

/// <summary>Cancellation and database ownership for one running job.</summary>
internal sealed record RunningJobRegistration(CancellationTokenSource Cancellation, string? Profile);
