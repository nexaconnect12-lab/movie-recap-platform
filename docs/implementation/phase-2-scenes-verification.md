# Phase 2 scene verification — 2026-10-13 (Asia/Singapore)

Scope: separately proposed and confirmed selected-video PySceneDetect slice. Existing solution/instructions were inspected. Source media, original hosts and application database were preserved. No later phase was implemented.

## Environment and commands

.NET SDK 10.0.400/net10.0, real local PostgreSQL in the existing Docker container, actual FFprobe/FFmpeg 9.0.2 and a newly created workspace-local Python 3.12.14 virtual environment. Installed pinned PySceneDetect 0.7.1, PyAV 19.0.0, OpenCV 5.0.0.93 and requirements.txt dependencies; no system-wide installation. Synthetic cut/multi-video/VFR/offset fixtures were generated with `-n` exclusively from owned color inputs. Credentials were loaded from the existing protected helper and were not printed. SDK outputs were isolated from binaries loaded by original hosts.

```powershell
. ./scripts/Set-SceneVerificationEnvironment.ps1
docker compose -f infrastructure/compose.yml up -d --wait
dotnet build MovieRecapPlatform.slnx -m:1
dotnet test MovieRecapPlatform.slnx -m:1 --no-build --logger 'trx;LogFilePrefix=scenes' --results-directory TestResults/scenes
dotnet ef migrations has-pending-model-changes --project src/Infrastructure --no-build
dotnet ef migrations script --project src/Infrastructure --no-build --idempotent --output artifacts/phase2-verification/scene-migrations.sql
```

TEST_POSTGRES, TEST_FFPROBE and TEST_SCENE_PYTHON were populated. Each integration test creates and removes its uniquely named database/media copies/ports; separate-host tests start and stop only their own processes. The helper applies no application migrations and starts no hosts. Initial restricted restore could not reach package audit metadata; the permitted network restore/build resolved it without disabling vulnerability auditing. Build was rerun after final documentation/cleanup and again passed with no pending model changes.

## Results

| Check | Result |
|---|---|
| Required solution build | Passed, 0 warnings, 0 errors |
| Unit/domain/architecture/adapter tests | 82 passed, 0 failed, 0 skipped |
| Real PostgreSQL/FFprobe/PySceneDetect/API/host tests | 46 passed, 0 failed, 0 skipped |
| Chosen nonprimary video / single scene | Actual decode passed; stream 1 produced 3 scenes, stream 0 produced 1 |
| VFR / nonzero timestamp offset | Actual PTS mapping passed; 20-frame VFR clip ends at 2.9 seconds; source offset retained |
| Subprocess timeout/output/malformed/cancellation | Stable failure/cancellation and process termination passed |
| Concurrent request replay / history / changed selection | Passed on PostgreSQL; changed selection rejects completion and preserves old success |
| Pause/cancel/expiry/recovery/source corruption | Fenced result writes and unchanged-source checks passed |
| Trigger-injected completion fault | No partial ranges/analysis/job success; transaction rollback passed |
| Upgrade from MediaStreamSelection | Existing import/asset/inventory/selection/source retained; no fabricated scenes |
| Separate API + worker scene smoke | Actual Python completed persisted expected ranges/provenance/audit/history |
| Existing import/discovery/stream host smokes | Passed in full suite |
| EF pending model changes | None |
| Generated idempotent migration Up SQL | Additive; 0 DROP/DELETE statements |
| Python runner syntax | Passed |
| PowerShell parser | All 9 scripts passed |
| Test cleanup | No recap_test_* databases or project testhost processes remained |
| Original hosts | API 30980 and worker 70964 remained running (verified outside sandbox) |

Total **128 passed, 0 failed, 0 skipped**. TRX: [46 integration tests](../../TestResults/scenes/scenes_net10.0_20261013051323.trx), [82 unit tests](../../TestResults/scenes/scenes_net10.0_20261013051326.trx). [Reviewed migration SQL](../../artifacts/phase2-verification/scene-migrations.sql).

Separate-host scene evidence: analysis `3d21e999-a538-40b4-81a5-88fd44efd2b5`, job `4a38ddd5-eb58-4b2e-ab44-0c12b2fbd43d`, chosen nonprimary global stream 1, ranges `[0,2)`, `[2,4)`, `[4,6)`, actual PySceneDetect 0.7.1. A later selection change marked that result noncurrent without changing ranges. Original bytes/write timestamp were preserved. These identities belonged to a test database removed afterward.

## Changed areas and limitations

Added VideoProcessing Domain/Application, selected-video Media Application contracts/Media-owned transaction adapter, real Python runner and pinned dependencies, bounded C# subprocess adapter, PostgreSQL operation/range persistence, additive VideoSceneAnalysis migration/snapshot, readiness, authenticated request/history routes and worker job dispatch. Added 13 unit cases and 10 integration/host cases, fault-only subprocess modes, owned fixtures and verification helper. Updated README/architecture/database/API/deployment/implementation/testing/roadmap and ADR 0008.

The application database and existing hosts were not upgraded/restarted. Activate by applying migrations and rebuilding/restarting configured hosts with a worker-accessible pinned Python environment and matching trusted media roots/FFprobe settings. See [configuration/API](../api/scene-analysis.md). PostgreSQL remains running; isolated binaries, tools, SQL and TRX remain in artifacts/TestResults.

Limitations: full decoding and two source hash passes are expensive; progress is coarse; timestamp memory is bounded by frame count; decoder allocation precedes frame pixel checks, so this is not an OS memory sandbox. Decoder support is separate from probe eligibility. Missing/nonmonotonic timestamps or final duration fail explicitly. ContentDetector boundaries are technical changes, not semantic labels, and may split motion/miss fades. Roots remain trusted and media reference-only; ordinary Windows share locks are not protection from hostile privileged filesystem mutation. Rights/operator identities remain assertions under shared-key access and rights remain Unreviewed. No service installation/production deployment was tested.

Next proposed Phase 2 task: source-preserving selected-audio extraction with managed artifact promotion/cleanup and fenced execution, subject to separate scope confirmation. Audio/subtitle extraction, thumbnails, preprocessing, AI, Burmese TTS, rendering, dashboard and publishing are absent from this implementation.

Historical slice evidence. Latest full-suite verification: [Phase 2 audio](phase-2-audio-verification.md).

Historical slice evidence. Latest full-suite verification: [Phase 2 frames](phase-2-frames-verification.md).

Historical slice evidence. Latest full-suite verification: [Phase 2 preparation](phase-2-preparation-verification.md).
