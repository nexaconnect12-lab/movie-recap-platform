# Phase 2 subtitle verification — 2026-10-14 (Asia/Singapore)

The confirmed selected embedded SubRip slice is implemented and verified. This implementation preserved the activated application database/release/hosts and original media. Verification used isolated build output, real tools, test-owned temporary storage and disposable PostgreSQL databases. No later phase or live activation was performed.

## Exact results

| Check | Result |
|---|---|
| `dotnet build MovieRecapPlatform.slnx -m:1` after implementation and final test enhancement | Passed, 0 warnings, 0 errors |
| `dotnet test MovieRecapPlatform.slnx -m:1 --no-build --logger trx --results-directory TestResults/subtitles` | **128 unit + 86 integration = 214 passed; 0 failed, 0 skipped** |
| Final enhanced legacy migration case, `--filter FullyQualifiedName~SubtitleTests.Additive_upgrade` | 1 passed, 0 failed/skipped |
| `dotnet ef migrations has-pending-model-changes --project src/Infrastructure --no-build` | No pending model changes |
| Generated idempotent Up SQL | Additive; no DROP/DELETE statements |
| PowerShell parser | All 17 scripts passed |
| Activated database preservation | Still 9 migrations, 28 jobs, original 6 foundation audit rows |
| Activated release/processes | All 79 file hashes unchanged; API 22100 / worker 28932 same identity/start time; readiness 200 |
| Test database cleanup | Zero `recap_test_*` databases remained |
| Original multi-stream fixture | SHA-256 matches retained activation receipt |

[86 integration TRX](../../TestResults/subtitles/Thet_Lwin_Oo_DESKTOP-48HK0EJ_2026-10-14_12_40_57_net10.0.trx), [128 unit TRX](../../TestResults/subtitles/Thet_Lwin_Oo_DESKTOP-48HK0EJ_2026-10-14_12_44_34_net10.0.trx), [enhanced legacy upgrade TRX](../../TestResults/subtitles-legacy/Thet_Lwin_Oo_DESKTOP-48HK0EJ_2026-10-14_12_45_54_net10.0.trx), [reviewed migration SQL](../../artifacts/phase2-verification/subtitle-migrations.sql).

Commands were run after dot-sourcing `scripts/Set-SubtitleVerificationEnvironment.ps1`, using real `TEST_POSTGRES`, FFprobe/FFmpeg 9.0.2 and the existing pinned scene Python environment. No integration prerequisite was skipped. Successful processing was not mocked; fault subprocesses emit only errors/hangs/oversized output.

## Meaningful coverage

Added 15 unit cases and 13 integration cases (10 subtitle, 2 preparation, 1 separate-host smoke). Unit coverage includes strict UTF-8/BOM/CRLF/multiline roundtrip, ordered overlapping source-time cues, malformed/negative/reversed/empty times, invalid bytes/controls/oversized text, preparation binding/schema/hash behavior, reserved jobs, actual artifact promotion/hash/cue revalidation, unowned/overlapping storage, subprocess timeouts/output caps/source rejection and physical process-tree cancellation.

Real PostgreSQL/FFmpeg cases verify explicit global SubRip stream 5, canonical bytes/hash/cues, concurrent replay/parameter conflicts, retained rights/source bytes/write times, stale selection publication fences/history, audit-trigger rollback after promotion with atomic cue/reference/job state, active/crashed orphan protection/cleanup, pause/cancel/expiry and expiry during promotion, second-attempt recovery, unchanged-timestamp source corruption, artifact corruption/limits, protected API routes/downloads/cleanup and rejection of generic orphan jobs. Temporary remuxes from owned inputs verify multiline UTF-8 at **5,000–6,000 source milliseconds** and actual **mov_text** rejection without a new extraction job.

Preparation tests require selected subtitle output, retain the actual SRT read handle, block ordinary Windows writes during checking, record corrupted/missing-file issues, reject fresh approval after corruption, stale prior decisions after new subtitle output, and reject racing publication during approval. No-subtitle preparation remains valid under existing tests. New binding version 2 persists subtitle identity; migration defaults old validation/audit bindings to version 1. The final enhanced migration case creates a real historical rejection/audit under the old column contract, upgrades twice, and verifies unchanged hash/review/history and version-1 calculations, preserved prior import/rights/queued scenes and no invented subtitle results.

Separate rebuilt API/worker smoke evidence: import **9258209f-6d98-409a-b4d0-286fc42d7baf**, extraction **6f842999-ede4-4156-95d1-a69adf053a53**, validation **9f417fe1-b6c5-4d2b-b33b-a84281d2c37b**. Actual source/SubRip/scene/PNG checks passed, operator approval became current Approved, then removing the subtitle choice made it Stale. Historical SRT remained downloadable with identical bytes; source bytes/write timestamps were preserved. These identifiers belonged to a removed disposable database, not the activated application database.

Earlier checks caught missing test namespace imports, the internal job-kind registration, a test lambda, singular/plural artifact configuration and a frozen test-clock completion tie. These were corrected; failed/intermediate checks are not counted as verification. The final full 214-test run passed. The subsequent legacy-history enhancement changed only its test assertions/setup and passed after a clean rebuild; production code remained unchanged.

## Changed areas and activation boundary

Added VideoProcessing Domain/Application subtitle entities/cues/ports/services/execution; Media selected-subtitle contracts/adapters; actual FFmpeg/SubRip/storage/PostgreSQL adapters; schema/readiness/registration; reserved internal job kind/namespace and worker/API composition. Preparation snapshot/history/report/approval now binds/verifies selected subtitles with compatible historical schema versions. `20261014044051_VideoSubtitleExtraction` adds two Video tables and four compatibility columns, bringing the latest source model to 21 application tables/10 migrations.

Added subtitle/API/implementation/verification docs, ADR 0013, verification environment helper and tests. Updated README, architecture/domain/module/pipeline, database, preparation API, deployment/testing/roadmap/plan and fixture documentation. No NuGet/provider installation, new committed media input, service installation or publishing credential is added.

The activated database remains at nine migrations/19 application tables. Its release/profile/source/artifact folders and running host identities are unchanged. Activate separately: inventory jobs/storage, back up database and referenced artifacts, restore-verify the backup, publish a new isolated release, apply the reviewed migration and restart both hosts with consistent `Subtitles__Root`/tool configuration, then rehearse owned-fixture acceptance. Earlier selected-subtitle approvals need extraction/revalidation/new decisions; migration fabricates no approvals. No-subtitle fingerprints remain compatible.

## Limits and next work

Initial support is embedded SubRip only. Empty, malformed and unsupported tracks fail explicitly. Export validates source-time cues without scene normalization, subtitle/video alignment guarantees, translation, OCR or semantic review. Text is retained as produced by FFmpeg; original packet formatting equivalence is not claimed. Cue responses are bounded per extraction but unpaginated; long histories and full-file hashing can be expensive. Trusted local storage/identities, synchronized clocks and consistent roots remain assumptions. No global retention/quota, OS decoder memory sandbox, production roles/TLS or continuous integrity monitor is added.

Preparation approval remains a technical checkpoint; rights remain unchanged/Unreviewed and grant no publication permission. Burmese UTF-8 text tests encoding, not voice or translation support. ASR/AI, generated narration subtitles, TTS, recap rendering, UI and publishing remain absent. Next work is separately scoped subtitle activation/acceptance; other preprocessing/codec support and later phases require separate scope.
