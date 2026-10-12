# Implementation roadmap
Phase 1: foundation hosts, job module, PostgreSQL migration, API, Windows-compatible worker, health/logging/config, tests, guides and agent instructions. See phase-1.md and verification.md.

Phase 2 manual import slice: implemented MediaAsset, import-scoped MediaRights and MovieImport with migrations, authenticated manual API, configured folders, stable-file/format validation, SHA-256 duplicates, real FFprobe metadata and job integration. See phase-2.md and phase-2-verification.md for evidence/limits.

Next task (remaining Phase 2): folder monitoring, fuller stream handling/preprocessing and PySceneDetect integration. These are not implemented in the manual-import slice.

Phase 3: chunked ASR/frame analysis, grounded outline, recap generation/versioned editor/approval.
Phase 4: Burmese language adapter, verified real TTS voice, chunks/normalization, aligned subtitles and exports.
Phase 5: timelines, FFmpeg rendering/mixing/burning, landscape/vertical validation and cancellation cleanup.
Phase 6: React/TypeScript/Vite administration UI and monitoring.
Phase 7: protected accounts, official publishing adapters, rights gates, persistent Quartz timezone schedules and publication deduplication.
Phase 8: resource/performance controls, authentication/security review, metrics/alerts, fault recovery and deployment automation.

Remaining foundation hardening: separate runtime DB role provisioning, migration rollback rehearsal, worker fleet liveness/metrics, audit retention, graceful shutdown budgeting for long-running handlers, API rate limits/user roles, secure production secret-provider integration and SCM lifecycle acceptance.

