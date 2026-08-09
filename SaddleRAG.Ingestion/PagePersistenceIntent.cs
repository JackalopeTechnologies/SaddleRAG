// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion;

internal enum PagePersistenceIntent
{
    None = 0,
    UpdateIfClassified = 1,
    UpsertAlways = 2
}
