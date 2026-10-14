# Implementation roadmap
Phase 1: foundation hosts, job module, PostgreSQL migration, API, Windows-compatible worker, health/logging/config, tests, guides and agent instructions. See phase-1.md and verification.md.

Phase 2 manual import slice: implemented MediaAsset, import-scoped MediaRights and MovieImport with migrations, authenticated manual API, configured folders, stable-file/format validation, SHA-256 duplicates, real FFprobe metadata and job integration. See phase-2.md and phase-2-verification.md for evidence/limits.

Phase 2 discovery slice: optional worker folder monitoring, durable discovery revisions/inbox APIs, stable/readable observation, explicit rights/source submission and atomic import-job integration. See phase-2-discovery.md and phase-2-discovery-verification.md for evidence/limits. Monitoring does not automatically import files.

Phase 2 stream slice: full real FFprobe inventories, immutable content metadata, per-import explicit/versioned selection/audit, source-hash verification and real legacy reinspection jobs. See phase-2-streams.md and phase-2-streams-verification.md. No extraction/preprocessing is implied.

Phase 2 scene slice: real PySceneDetect selected-video decode through PyAV, timestamp-derived persisted ranges/provenance, historical results and transactional fenced jobs. See phase-2-scenes.md and phase-2-scenes-verification.md.

Phase 2 audio slice: real selected-track FFmpeg decode into validated PCM WAV, owned temporary/promotion/cleanup lifecycle, source/selection binding, authenticated download/history and fenced jobs. See phase-2-audio.md and phase-2-audio-verification.md.

Phase 2 frame slice: real selected-video scene quantile PNGs, actual PTS/offset mapping, owned batch artifacts/fenced jobs/download/history/cleanup. See phase-2-frames.md and phase-2-frames-verification.md.

Phase 2 preparation slice: combined current preparation view, real source/WAV/PNG validation jobs, explicit snapshot bindings, versioned operator decisions/notes/audits and stale-review detection. See phase-2-preparation.md and phase-2-preparation-verification.md.

Phase 2 activation: application database migrated, isolated hosts activated, protected backups restore-verified, and real owned-fixture preparation accepted with cancellation/recovery/staleness/duplicate/source-preservation checks. See [activation evidence](phase-2-activation.md) and [deployment guide](../deployment/phase-2-activation.md).

Phase 2 subtitles: real selected embedded SubRip extraction into validated UTF-8 SRT/cues, managed fenced artifacts and subtitle-aware preparation checks/staleness. See phase-2-subtitles.md and phase-2-subtitles-verification.md. Activated hosts/database are preserved.

Next work: separately scoped activation/acceptance of the subtitle slice. Other codecs/preprocessing and ASR/AI/TTS/rendering/publishing remain separate scope; this does not claim all Phase 2 capabilities complete or authorize Phase 3.

Phase 3: chunked ASR/frame analysis, grounded outline, recap generation/versioned editor/approval.
Phase 4: Burmese language adapter, verified real TTS voice, chunks/normalization, aligned subtitles and exports.
Phase 5: timelines, FFmpeg rendering/mixing/burning, landscape/vertical validation and cancellation cleanup.
Phase 6: React/TypeScript/Vite administration UI and monitoring.
Phase 7: protected accounts, official publishing adapters, rights gates, persistent Quartz timezone schedules and publication deduplication.
Phase 8: resource/performance controls, authentication/security review, metrics/alerts, fault recovery and deployment automation.

Remaining foundation hardening: separate runtime DB role provisioning, migration rollback rehearsal, worker fleet liveness/metrics, audit retention, graceful shutdown budgeting for long-running handlers, API rate limits/user roles, secure production secret-provider integration and SCM lifecycle acceptance.



