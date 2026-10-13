# Phase 2 manual-import verification — 2026-10-12 (Asia/Singapore)
This is the original manual-import acceptance snapshot. Current combined import/discovery acceptance is in [discovery verification](phase-2-discovery-verification.md).

Scope: first Phase 2 manual-import slice only. Implementation was confirmed before pre-existing files were modified. No user media was altered. No source control checkout was present at this directory, so no commit/PR was created.

## Verification environment and commands

.NET SDK 10.0.400, net10.0 targets; existing PostgreSQL 17 local Docker service; real FFprobe 9.0.2 Gyan essentials Windows build. The workspace-local bundle was downloaded from the [Windows build provider](https://www.gyan.dev/ffmpeg/builds/) linked by [ffmpeg.org](https://ffmpeg.org/download.html). Archive SHA-256 matched the provider checksum: `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`. It lives under ignored artifacts/phase2-tools; no system installation was performed.

Existing API/worker processes were running from normal Debug folders. They were preserved. The verification helper enables SDK artifact output isolation and loads the existing DPAPI-protected local credentials into the environment without printing them.

```powershell
. ./scripts/Set-Phase2VerificationEnvironment.ps1
docker compose -f infrastructure/compose.yml up -d --wait
dotnet build MovieRecapPlatform.slnx -m:1
dotnet test MovieRecapPlatform.slnx -m:1 --no-build --logger 'trx;LogFilePrefix=phase2_acceptance' --results-directory TestResults/phase2
dotnet ef migrations has-pending-model-changes --project src/Infrastructure --no-build
dotnet ef migrations script --project src/Infrastructure --no-build --idempotent --output artifacts/phase2-verification/migrations.sql
```

TEST_POSTGRES and TEST_FFPROBE must both be populated for the complete suite. If the local bundle is absent, set TEST_FFPROBE to an existing executable before dot-sourcing the helper. Every PostgreSQL test creates/migrates/drops only its own random test database. Separate-host smoke starts its own API/worker on a temporary database and free loopback port, drains process output, and terminates only the processes it started.

## Results

Final acceptance results:

| Check | Result |
|---|---|
| `dotnet build MovieRecapPlatform.slnx -m:1` with isolated artifact environment | Passed; 0 warnings, 0 errors |
| Unit / architecture / adapter tests | 47 passed, 0 failed, 0 skipped |
| PostgreSQL / FFprobe / API / separate-host tests | 16 passed, 0 failed, 0 skipped |
| Fresh migration and upgrade preserving existing Phase 1 job/audit | Passed on real PostgreSQL; repeated migration is safe |
| Rights-expiry precision and replay | Passed on real PostgreSQL |
| Actual FFprobe fixtures | MP4, MKV, MOV, AVI plus video-only MP4; original bytes/write timestamps preserved |
| Separate API + worker smoke | Real import and duplicate succeeded; corrupt fixture failed with `ffprobe-rejected-media`; original bytes preserved |
| Pending EF model changes | None |
| Idempotent migration Up SQL generation/review | Passed; additive tables/indexes/constraints, no DROP/DELETE statements |
| PowerShell parsing | All 8 scripts passed |
| Test resource cleanup | No `recap_test_*` databases or project testhost processes remained |
| Original API / worker | Original process IDs 30980 / 70964 still running |

Final TRX evidence: [integration/media/host tests](../../TestResults/phase2/phase2_acceptance_net10.0_20261012203229.trx), [unit/architecture/adapter tests](../../TestResults/phase2/phase2_acceptance_net10.0_20261012203231.trx). Total: **63 passed, 0 failed, 0 skipped**.

Final separate-host smoke: import `a781458c-8e2f-4109-897d-428c2b3ae9ac`, job `5c255769-b0ba-499c-968b-92435c1d7290`, duplicate import `4c511178-4652-4507-802d-6438a243c7b8`. These identities were in the disposable verification database, which was removed afterwards. SQL evidence: [generated Up script](../../artifacts/phase2-verification/migrations.sql).

Initial checks exposed environmental/output locks and NuGet audit network access restrictions; network-enabled restore and isolated outputs resolved those. Test-harness defects corrected during verification: cold-start HTTP timeout, Windows junction command quoting/bounded termination, and comparing pre-save .NET ticks to PostgreSQL microseconds. Rights expiry also now normalizes to microsecond precision so idempotent replay is reliable. Failing intermediate TRX files remain alongside final evidence for transparency; they are not counted as passing verification.

## Changed areas

- Media Domain/Application projects and solution references: immutable content metadata, import results, rights assertions, inspection/store ports and orchestration.
- Jobs: supported internal import kind, reserved public key namespace and transactional create/query/complete contract.
- Infrastructure: local-file validation/stability/hashing, bounded cancellable FFprobe, media store, shared-transaction Jobs adapter, model mapping, migration/snapshot and readiness checks.
- API/worker: authenticated import/status routes, error mapping, media configuration, dispatch, coarse progress and safe failure codes.
- Tests: owned fixture media, subprocess fault fixture, unit/architecture/adapter checks, real PostgreSQL/FFprobe/API/concurrency/recovery/upgrade checks and two-host smoke.
- README, architecture/domain/rights/module/database docs, API usage, implementation/roadmap/plan/testing docs, ADR 0005 and verification environment helper.

## Local state and limits

The existing application database was not upgraded automatically while its older hosts were running. The additive migration was applied to disposable test databases, including an upgrade preserving existing Phase 1 jobs/audits. Existing API/worker processes remain unchanged; test processes and test databases are cleaned. The existing PostgreSQL service remains running. Verification binaries, tool bundle, migration SQL and TRX evidence remain under artifacts/TestResults. Owned fixtures remain under tests/Fixtures.

To activate manual import locally, stop/restart the existing hosts with the new build after applying the migration, and configure matching folder aliases/roots and a real FFprobe path in both hosts. Preserve user media and keep roots trusted/stable. Sources are referenced in place, not archived; filesystem removal/replacement by another actor remains outside the preservation guarantee. Hashing reads each file twice; progress is coarse; the worker remains concurrency 1. FFprobe verifies metadata/container/video presence, not full decoding. MP4/MOV share a demuxer family. Rights remain caller assertions and Unreviewed; no publishing permission is inferred.

Next scoped work: remaining Phase 2 folder monitoring, fuller stream handling/preprocessing and scene detection. None of those, AI, Burmese TTS, rendering or publishing was implemented here. No service installation, production deployment or later phase acceptance is claimed.

Historical slice evidence. Latest full-suite verification: [Phase 2 scenes](phase-2-scenes-verification.md).

Historical slice evidence. Latest full-suite verification: [Phase 2 audio](phase-2-audio-verification.md).

Historical slice evidence. Latest full-suite verification: [Phase 2 frames](phase-2-frames-verification.md).

Historical slice evidence. Latest full-suite verification: [Phase 2 preparation](phase-2-preparation-verification.md).
