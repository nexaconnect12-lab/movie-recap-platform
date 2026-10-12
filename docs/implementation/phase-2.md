# Phase 2 manual import implementation
This documents the original manual-import slice. Subsequent folder monitoring and discovery inbox are described in [the discovery slice](phase-2-discovery.md); stream/scene work remains deferred.

Confirmed scope: configurable local media folders, rights/source records, stability/format validation, SHA-256 deduplication, real FFprobe metadata, migrations and fenced job integration. This is the first Phase 2 slice; it does not complete all Phase 2 work.

Added Media Domain/Application projects, Infrastructure inspector/FFprobe/store adapters, Jobs transaction contract, authenticated import/status endpoints and worker dispatch for `media.manual-import`. Source files remain in place. Import/domain invariants and metadata are independent of framework persistence. Per-import rights are caller assertions and remain unreviewed for publication.

The additive `20261012121403_ManualMediaImport` migration adds MediaAssets, MovieImports and MediaRights in the `media` schema, unique content/request/job indexes and restrictive local references. Existing Jobs migrations are preserved. Infrastructure composes Media with Jobs through `IJobTransaction`; creation and completion/audit writes are atomic. Repeatable-read status snapshots avoid mixing newly succeeded jobs with older import data. Advisory locks serialize equal request keys/hash values. Domain leases fence completion and rollback all media writes on stale/cancelled executions.

Tests include owned synthetic media (four supported containers and video-only MP4), real FFprobe, concurrent request and content deduplication, metadata conflict rollback, API access/validation, immutable rights per source, pause/cancel/expiry fencing, recovery/retry, format parsing, file locks/stability/cancellation, junctions/links, and bounded subprocess output/timeouts/termination. The test-only ProbeProcessFixture intentionally produces process failures; it never supplies successful production metadata.

See [verification](phase-2-verification.md) for exact commands/results and [API/configuration](../api/media-imports.md) for usage. ADR 0005 records design decisions and limits.

Remaining Phase 2 work: folder monitoring, fuller stream handling/preprocessing and PySceneDetect scenes with their own module contracts and owned fixtures. Later phases remain unimplemented. Operational limits: trusted stable source roots, reference-only storage, two full hash passes, single-job worker concurrency, coarse progress, metadata inspection without decoding, shared-key API access, and no legal review/publication gate.
