# Implementation roadmap
Phase 1: foundation hosts, job module, PostgreSQL migration, API, Windows-compatible worker, health/logging/config, tests, guides and agent instructions. See phase-1.md and verification.md.

Phase 2 manual import slice: implemented MediaAsset, import-scoped MediaRights and MovieImport with migrations, authenticated manual API, configured folders, stable-file/format validation, SHA-256 duplicates, real FFprobe metadata and job integration. See phase-2.md and phase-2-verification.md for evidence/limits.

Phase 2 discovery slice: optional worker folder monitoring, durable discovery revisions/inbox APIs, stable/readable observation, explicit rights/source submission and atomic import-job integration. See phase-2-discovery.md and phase-2-discovery-verification.md for evidence/limits. Monitoring does not automatically import files.

Phase 2 stream slice: full real FFprobe inventories, immutable content metadata, per-import explicit/versioned selection/audit, source-hash verification and real legacy reinspection jobs. See phase-2-streams.md and phase-2-streams-verification.md. No extraction/preprocessing is implied.

Next task (remaining Phase 2): PySceneDetect scene detection using the selected video stream, with persisted time ranges, cancellation/recovery and owned fixtures. Stream extraction/preprocessing remains separately scoped work; AI/TTS/rendering/publishing remain later phases.

Phase 3: chunked ASR/frame analysis, grounded outline, recap generation/versioned editor/approval.
Phase 4: Burmese language adapter, verified real TTS voice, chunks/normalization, aligned subtitles and exports.
Phase 5: timelines, FFmpeg rendering/mixing/burning, landscape/vertical validation and cancellation cleanup.
Phase 6: React/TypeScript/Vite administration UI and monitoring.
Phase 7: protected accounts, official publishing adapters, rights gates, persistent Quartz timezone schedules and publication deduplication.
Phase 8: resource/performance controls, authentication/security review, metrics/alerts, fault recovery and deployment automation.

Remaining foundation hardening: separate runtime DB role provisioning, migration rollback rehearsal, worker fleet liveness/metrics, audit retention, graceful shutdown budgeting for long-running handlers, API rate limits/user roles, secure production secret-provider integration and SCM lifecycle acceptance.

