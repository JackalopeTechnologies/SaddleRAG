# Stage 7 RED handoff

These drafts are intentionally named `*.cs.stage7red`. They are not included by the SDK `**/*.cs`
compile glob and must remain outside compilation until Stage 6 is green and the Stage 7 RED gate is
explicitly opened.

## Draft acceptance coverage

- `Monitor/DocumentLifecycleDeletionTests.cs.stage7red`
  - delete-version removes revision, page, chunk, and subject-assignment state;
  - a source identity, subject catalog, and original artifact survive while a second version still
    references them;
  - delete-library removes the directory definition, final source/revision/subject records, and the
    final GridFS artifact references.
- `Database/DirectoryRenameLifecycleIntegrationTests.cs.stage7red`
  - library rename preserves the stable document id and exact artifact hashes/bytes;
  - library rename remaps directory, revision, catalog, assignment, page, chunk, URI, and citation
    references;
  - version rename preserves the stable document id while remapping version-scoped revision and
    assignment links;
  - an opaque `document-page-*` id is seeded deliberately so slash-segment-only rename logic cannot
    pass accidentally.
- `Mcp/DirectoryScanOrphanProtectionTests.cs.stage7red`
  - a running `JobType.DirectoryScan` protects candidate `(library, version)` pairs in dry-run and
    apply modes;
  - terminal scans do not protect real orphans.
- `Database/DirectoryOrphanCleanupIntegrationTests.cs.stage7red`
  - dry-run reports directory definitions, source documents, document revisions, subject catalogs,
    subject assignments, and only artifacts that would become unreferenced;
  - apply removes those stores while retaining a blob referenced by another valid library;
  - absolute roots never appear in the report.
- `Mcp/DirectoryLifecycleMutationToolsTests.cs.stage7red`
  - delete-version, delete-library, rename-library, and rename-version dry-runs include document and
    subject counts;
  - private roots are not returned;
  - dry-run remains read-only.
- `Packaging/DirectoryPackageV2Tests.cs.stage7red`
  - manifest v2, sanitized directory scan options, exact artifact streams and manifest hashes;
  - source/revision/catalog/assignment/provenance/citation JSONL records;
  - no machine root in any decompressed entry or entry name;
  - a manifest-v1 web package with no document sections remains importable.
- `Packaging/DirectoryPackagingRoundTripIntegrationTests.cs.stage7red`
  - full Mongo/GridFS export-delete-import round trip;
  - exact source and extraction bytes, hashes, extraction provenance, subjects, citations, and search;
  - imported directory definition is `Unbound` with no root but remains searchable;
  - failed import rollback removes package-created records and unique artifacts while preserving a
    pre-existing shared blob.
- `Packaging/Fixtures/DirectoryPackagingFixtures.cs.stage7red`
  - one deterministic directory-document package fixture shared by unit and integration drafts.

## Existing compiled tests to update only when Stage 7 RED is released

1. `MonitorDeleteCascadeTests.cs`
   - configure source-document, subject-assignment, and subject-catalog repositories in every factory
     fixture;
   - assert the new cascade counts and ordering without weakening the existing all-cleanup-attempts
     behavior.
2. `MutationToolsTests.cs`
   - add empty source/subject defaults to its shared factory fixture so old web-only cases remain
     unchanged;
   - merge the document/subject dry-run assertions from the Stage 7 draft or keep the new focused test
     class after renaming it to `.cs`.
3. `OrphanCleanupToolsTests.cs` and `OrphanCleanupActiveVersionTests.cs`
   - add empty source/revision/catalog/assignment/artifact inventories to existing fixtures;
   - extend `ByCollection`, pair flags, and deletion totals assertions;
   - merge the `DirectoryScan` active-job cases or retain the focused draft as a separate compiled
     class.
4. `RenameLibraryIntegrationTests.cs` and `RenameVersionIntegrationTests.cs`
   - retain their current web-record regression cases;
   - either merge the directory identity/citation/subject cases or compile the focused Stage 7 class
     alongside them.
5. `LibraryExporterTests.cs`, `LibraryImporterTests.cs`, `ImporterValidateBeforeDestroyTests.cs`,
   `PackagingRoundTripTests.cs`, and packaging MCP construction tests
   - provide empty `ISourceDocumentRepository`, `ISubjectCatalogRepository`, and
     `ISubjectAssignmentRepository` dependencies to existing web-only constructor call sites;
   - keep manifest-v1 synthetic fixtures free of v2-only sections;
   - keep existing validation-before-overwrite and per-version rollback assertions intact.

## Package v2 wire contract assumed by the RED drafts

- `manifestVersion` is `2`.
- `manifest.directory` is optional and contains only `recursive`, `allowedExtensions`, and
  `exclusionPatterns`; it never contains `rootPath` or a binding copied from the exporting machine.
- Library-level entries:
  - `documents/sources.jsonl`
  - `subjects/catalogs.jsonl`
  - `document-artifacts/{lowercase-sha256}.bin`
- Version-level entries:
  - `versions/{version}/documentRevisions.jsonl`
  - `versions/{version}/subjectAssignments.jsonl`
- Every new entry participates in the existing manifest SHA-256/byte-length validation.
- Import creates a directory definition with an empty root and `DirectoryLibraryBindingStatus.Unbound`.
- Manifest v1 omits all of these fields and entries; that absence is valid and imports through the
  existing web-only behavior.

## Release order

1. Rename only the smallest focused draft needed for the next implementation batch.
2. Run that draft once to record RED for missing Stage 7 behavior.
3. Implement the whole batch behind that acceptance boundary.
4. Run the focused tests once after the batch, then the approved Stage 7 gate.
5. Do not repeatedly rerun the broad ordinary suite between individual edits.
