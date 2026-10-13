# Phase 1 architecture and implementation plan
Inspected the supplied working directory on 2026-10-12: only empty work and outputs folders existed, with no solution or applicable AGENTS.md. Specification: attached Codex Master Prompt — AI Movie Recap Automation Platform (Pasted text.txt). Deliverables were initially created in outputs/MovieRecapPlatform. The canonical project location is now D:\DevOps\movie-recap-platform, as requested by the user; the existing matching copy was retained without replacing source files.

1. Create net10.0 projects and clean dependency boundaries.
2. Implement a Jobs domain aggregate with explicit states, attempts, progress, leases and version checks.
3. Implement PostgreSQL persistence, atomic claim and state/audit transactions; generate EF migrations.
4. Add validated API contracts, protected endpoints, health and OpenAPI.
5. Add a Windows-compatible worker, heartbeat, bounded execution and a real foundation database check.
6. Add unit/boundary tests and actual PostgreSQL/API concurrency and recovery tests.
7. Add local compose/setup and service scripts; document architecture, schemas, future modules and exact usage.
8. Build, run tests, apply migrations and smoke-test both processes. Record evidence and limitations.

Assumptions: .NET 10 SDK available; PostgreSQL 17 local Docker instance; no existing schema to preserve. Phase 1 has one job kind and one executable worker, resource concurrency = 1 per process. Future module schemas and Quartz arrive with their feature phases. No placeholder business entities, provider integrations or dashboard are counted as implemented. UTC timestamps assume synchronized host clocks.

Phase 2 manual-import changes were proposed after inspecting the existing solution/instructions and confirmed by the user on 2026-10-12. Scope and decisions are in phase-2.md and ADR 0005. Existing hosts were found running; verification uses separate artifact outputs and disposable databases/processes so those hosts and the persisted application database stay intact. No further phase is included.

Phase 2 discovery changes were separately proposed and confirmed on 2026-10-12. Implement optional worker-hosted monitoring, persistent versioned candidates and inbox APIs, safe/stable observation and atomic explicit source/rights submission into existing jobs. Source fingerprints bind the later executor to the observed revision. See phase-2-discovery.md and ADR 0006. Verification again uses isolated artifact outputs and disposable databases; existing application hosts/database are preserved.

Phase 2 stream changes were separately proposed and confirmed on 2026-10-13 (Asia/Singapore). Extend real FFprobe inventory, explicit per-import versioned choices/audits, stable source hash verification, additive storage and real legacy inspection jobs. See phase-2-streams.md and ADR 0007. Existing application hosts/database remain preserved while tests use isolated outputs/disposable databases. PySceneDetect and later phases are excluded.


Phase 2 scene changes were separately proposed and confirmed on 2026-10-13 (Asia/Singapore). Implement real PySceneDetect selected-video decoding through Media Application contracts, actual PTS ranges/provenance, bounded cancellable execution, additive Video storage and atomic fenced jobs/history. See phase-2-scenes.md and ADR 0008. Existing application hosts/database remain preserved; no extraction or later phases are included.

Selected-audio extraction was separately proposed and confirmed on 2026-10-13 (Asia/Singapore). Extend VideoProcessing with real FFmpeg extraction, a fixed validated PCM WAV profile, source/selection binding and managed per-lease artifacts. Use reference-gated publication and orphan reconciliation around atomic fenced job/audit completion. See phase-2-audio.md and ADR 0009. Existing application hosts/database remain preserved; ASR and later phases are excluded.

The scene thumbnail/frame-sampling slice was separately proposed and confirmed on 2026-10-13 (Asia/Singapore). Extend VideoProcessing using actual selected-stream FFmpeg decode, deterministic per-scene frame quantiles, original PTS/offset mapping and validated owned PNG batches. Reuse fenced artifact publication/cleanup and shared trusted-root guards; add authenticated status/history/download/cleanup, additive migration and real media/database/host tests. See phase-2-frames.md and ADR 0010. Existing application hosts/database remain preserved; later phases are excluded.
