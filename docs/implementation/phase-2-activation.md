# Phase 2 activation acceptance — 2026-10-13

The confirmed local activation is complete. Application database `recap` now has all nine migrations and 19 application tables (jobs 2, media 9, video 8), plus EF history. API PID 22100 and worker PID 28932 run the isolated Release binaries; final readiness returned 200. No later phase or service installation was implemented.

## Preservation and deployment

Before activation the database had two migrations, two succeeded foundation jobs, six audit rows and no media/artifact references. Protected backups under `.local/backups/activation-20261013-01` retain previous API/worker binaries, DPAPI credentials, initial and final-quiesced custom PostgreSQL dumps and hash/verification manifest. Both dumps were restored into separate disposable databases and verified at 2 migrations/2 jobs/6 audits, then those databases were dropped. ACLs restrict the backups to the current Windows identity, SYSTEM and Administrators.

Idle old API 30980 and worker 70964 were verified by executable path and stopped. Seven additive Up migrations were applied explicitly. The original foundation jobs and six audits remain intact. Original Debug directories remain; new API/worker plus SHA-256 manifest are in `.local/runtime/releases/activation-20261013-01`. The nonsecret `.local/runtime/active.json` profile injects shared rehearsal roots/tools; credentials remain protected and environment-injected.

Final database status was 25 Succeeded and 3 Cancelled jobs, zero Queued/Running/RetryWait. Partial rehearsal history was retained. Only application database `recap` remained among `recap*` names.

## Exact checks

| Check | Result |
|---|---|
| `dotnet build MovieRecapPlatform.slnx -m:1` with isolated artifact configuration, including final repeat | Passed; 0 warnings/errors |
| `dotnet test MovieRecapPlatform.slnx -m:1 --no-build --logger trx --results-directory TestResults/activation` | 113 unit + 73 real integration passed; 0 failed/skipped |
| Both `dotnet publish ... -c Release --self-contained false -m:1` | Passed |
| `dotnet ef database update --project src/Infrastructure --no-build` | Seven pending migrations applied |
| `dotnet ef migrations has-pending-model-changes --project src/Infrastructure --no-build` | No pending changes |
| Both initial/quiesced dump restores | Passed, expected baseline counts |
| `scripts/Rehearse-Phase2.ps1` | Clean complete run passed, lifecycle included |
| `scripts/Test-ActivatedHostGuards.ps1` | 6 passed: duplicate starts, stale start times and wrong executables rejected; hosts remained running |
| Real scene Python `tests/Operational/test_rehearsal_artifacts.py -v` | 6 passed: valid output, wrong track/rate/color, missing padding and truncated PNG |
| PowerShell parser | All 16 scripts passed |
| Final readiness / preserved foundation audits | 200 / 6 |

TRX: [73 integration](../../TestResults/activation/Thet_Lwin_Oo_DESKTOP-48HK0EJ_2026-10-13_11_31_30_net10.0.trx), [113 unit](../../TestResults/activation/Thet_Lwin_Oo_DESKTOP-48HK0EJ_2026-10-13_11_34_27_net10.0.trx). Real PostgreSQL, FFprobe/FFmpeg 9.0.2 and the existing pinned scene Python environment were used. No successful media processing was simulated.

Two earlier rehearsals stopped on harness defects: PowerShell converted a JSON timestamp to DateTime, defeating string comparison; then an assertion wrapper nested a JSON audit array. The first identity guard prevented the crash. The second worker crash actually recovered on attempt 2 with passed report and lease-recovered audit. Date comparisons/array handling were corrected. Their partial imports and history remain; neither incomplete run counts as full acceptance.

## Clean acceptance

[Full receipt and live IDs](../../.local/runtime/receipts/rehearsal-ad647a37f6844d39a7980d47bde87f45/acceptance.json), with independently downloaded WAV/PNG files alongside it.

Primary import **535ac7cd-83d7-4f51-b30e-308779ddb57c**, asset **689e1d6f-a54e-45f6-9d11-f6d666b13713**. A unique repository-owned fixture copy imported with matching SHA-256 and all six streams. Explicit nondefault selections were video 1/audio 3/subtitle 5. Rights assert local technical-test use and remain Unreviewed.

Actual PySceneDetect produced one constant-blue scene. FFmpeg WAV had 16,000 mono/16 kHz/16-bit PCM samples; independent spectrum analysis measured 880 Hz rather than default-track 440 Hz. Three decoded blue 320×180 PNGs had black padding, frame indices 1/5/8 and actual timestamps 0.1/0.5/0.8 seconds. Integrity validation passed and explicit review became Approved.

Running job **0b9db9ee-e95b-40a9-a25f-a3ed6d7736aa** cancelled without publishing a report. Recovery job **bc01be3f-f232-4a68-ac58-a64949b28dbb** lost the tracked worker, waited for lease expiry/backoff and succeeded on **attempt 2**, passed=true, with lease-recovered audit. New validation made the earlier approval Stale. Changed video choice also made approval Stale; historical WAV remained downloadable with identical hash.

The selected blue video was restored under a new version and preparation regenerated/revalidated/reapproved. Final validation **efc8ede1-7ac8-4b80-87c7-2dcf5cd6341a** passed; snapshot **97226dec699b11dd4d7e84acb81240d437963d605d6a82eb5d3132d62b572d93** was current Approved. Duplicate import reused the same asset with IsDuplicate=true. Original fixture and imported-copy bytes/write timestamps remained unchanged.

## Changes, limitations and next work

Added local environment/start/stop helpers, reusable rehearsal, independent artifact verifier, six Python tests and six process guard checks. README, architecture, database/deployment/implementation/testing/roadmap docs and ADR 0012 document activation/rollback. Module behavior/schema definitions did not change. See [operational commands and rollback](../deployment/phase-2-activation.md).

This is local console activation; hosts do not restart after reboot, discovery is disabled, and shared-key actors/rights remain assertions. Technical approval grants no publication permission or continuous integrity monitoring. Receipts/artifacts/console logs have no global retention/quota. An existing EF diagnostic about shared immutable owned binding instances appeared during validation commits; reports and recovery passed. Resolving that diagnostic is separate maintenance. Disposable restore verification does not prove destructive live rollback or cross-machine recovery.

No ASR/AI, Burmese TTS, recap rendering, UI or publishing is implemented. Next work is to scope remaining Phase 2 preprocessing, such as selected subtitle extraction. Later phases, production security, retention, service installation and cross-machine recovery remain separate scope.
