// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Outcome of an import-owned subject-catalog publication rollback.
/// </summary>
public enum ImportCatalogRollbackOutcome
{
    RolledBack,
    AlreadyCandidate,
    ReferencedBySurvivor,
    NotOwned
}
