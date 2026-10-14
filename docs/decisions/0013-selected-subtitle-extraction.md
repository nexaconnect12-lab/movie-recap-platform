# ADR 0013: Selected embedded SubRip extraction

Accepted 2026-10-14, within the confirmed Phase 2 subtitle slice.

Media owns selected-track/source inspection and transactional choice checks through new Application contracts. VideoProcessing owns extraction/cue entities, requests, execution and artifact ports. Infrastructure supplies actual FFmpeg, strict SubRip validation, owned local storage and PostgreSQL persistence. Jobs supplies durable lifecycle/fencing through its existing contracts; no cross-schema relationships are introduced.

Initially support only embedded `subrip`. Reject other codecs explicitly before enqueueing. Canonical UTF-8 SRT preserves exported source timestamps and multiline text without translating, OCR or timeline rebasing. Strict cue/resource bounds and full byte/hash/cue comparisons apply before publication and on download/approval. Permit ordered overlaps; reject empty output rather than inventing success. The source remains held read-only and is verified before/after extraction.

Reuse the proven audio pattern: unique lease-attempt temporary output, exclusive lock, non-overwriting file promotion, advisory operation/import locks, selection validation and lease recheck after promotion. Commit subtitle cue rows, artifact reference and job/audit success in one database transaction. Reference-aware reconciliation preserves committed/active output and resolves crash/rollback orphans. Cancellation kills the subprocess tree; retries create new attempt files.

Selected subtitles become required preparation input. New completed output changes the snapshot fingerprint; actual subtitle verification participates in reports and fresh approval with retained read handles. Add nullable subtitle identity plus explicit binding schema version to historical validation/audit bindings. Default existing rows to schema version 1, preserve stored fingerprints and version-1 calculations, and create new bindings with version 2. No-subtitle fingerprints stay compatible. Historical selected-subtitle approvals require new extraction/check/decision under version-2 snapshots.

Migration adds Video-owned SubtitleExtractions/SubtitleCues and compatibility columns only. Verification uses real owned fixtures, disposable databases and isolated API/worker processes. Do not upgrade the activated local release/application database in this slice. Live activation, other subtitle codecs, OCR, ASR/AI, translation/TTS, rendering/publishing and production hardening require separate scope.
