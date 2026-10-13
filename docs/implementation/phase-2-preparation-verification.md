# Phase 2 preparation verification — 2026-10-13 (Asia/Singapore)

This records earlier isolated implementation verification. The subsequently confirmed [local activation and acceptance](phase-2-activation.md) migrated `recap` and restarted live hosts; deployment statements below describe that earlier verification boundary.

Scope: separately proposed and confirmed prepared-artifact review/validation slice. Existing solution/instructions/docs were inspected before authorized changes. Original media/import rights/application database/hosts were preserved. No later-phase implementation, simulated media completion or publication authorization was added.

## Environment and exact commands

.NET SDK 10.0.400/net10.0, actual existing local PostgreSQL, FFprobe/FFmpeg 9.0.2 and the previously pinned real PySceneDetect/PyAV Python environment. Existing owned multi-stream/cut fixtures generated actual scenes/audio/PNGs; no new fixture/provider/package/system tool/service was installed. Protected credentials were injected through the existing helper without printing secrets. SDK artifacts were isolated from binaries used by original hosts. Tests owned all generated databases/media copies/artifact roots/ports/processes.

```powershell
. ./scripts/Set-FrameVerificationEnvironment.ps1
dotnet build MovieRecapPlatform.slnx -m:1
dotnet test MovieRecapPlatform.slnx -m:1 --no-build --logger 'trx;LogFilePrefix=preparation_final' --results-directory TestResults/preparation
dotnet ef migrations has-pending-model-changes --project src/Infrastructure --no-build
dotnet ef migrations script --project src/Infrastructure --no-build --idempotent --output artifacts/phase2-verification/preparation-migrations.sql
```

All TEST_POSTGRES/TEST_FFPROBE/TEST_FFMPEG/TEST_SCENE_PYTHON prerequisites were populated. Migration generation used `dotnet ef migrations add VideoPreparationReview --project src/Infrastructure --no-build`. The new unapplied migration was regenerated during this implementation to include exact historical bindings, yielding `20261013011015_VideoPreparationReview`; no prior migration was removed and no application database downgrade/update occurred. Tests applied migrations only to disposable databases, including a real upgrade from VideoFrameSampling.

## Results

| Check | Result |
|---|---|
| Required solution build | Passed, 0 warnings, 0 errors |
| Unit/domain/architecture/adapter tests | 113 passed, 0 failed, 0 skipped |
| Real PostgreSQL/media/API/host tests | 73 passed, 0 failed, 0 skipped |
| Combined preparation/current choices | Real source/rights/tracks/scenes/audio/PNG metadata and bounded history passed |
| Source/WAV/PNG integrity validation | Actual held-handle reads/hashes/formats passed; no successful checks emulated |
| Incomplete/no-audio cases | Incomplete approval/validation rejected; noted rejection and explicitly unselected audio supported |
| Concurrent validation requests / changed parameters | One bound job/request; conflicting replay rejected |
| Concurrent review commands | One decision committed per version; other commands conflicted |
| Missing/corrupt WAV/PNG / changed source bytes | Passed=false with stable issues; prior approval became Stale after report |
| New frame batch / changed selection | Fingerprint changed; prior decision effectively Stale |
| Changes during validation/approval | Current-bundle recheck prevented stale report/decision commit |
| Exact historical bindings | Validation and approved audit retained source/selection/scene/audio/frame identities matching reviewed snapshot |
| Read-handle fencing / cancellation / pause / expiry / recovery | Source writer denied on Windows; stale/interrupted execution did not publish success |
| Trigger-injected report/decision audit faults | Review version/state/report/job success/audit rolled back atomically |
| Prior-schema upgrade | Actual completed scenes/audio/PNG files/import/rights retained; no invented reviews/reports/approvals |
| Authenticated API/version/hash/notes checks | Passed; stale commands conflicted; generic validation jobs rejected |
| Separate API + worker preparation smoke | Real checks, versioned approval/audit and Stale after selection change passed |
| Existing foundation/import/discovery/stream/scene/audio/frame tests | Passed in same full suite |
| EF pending model changes | None |
| Generated idempotent Up SQL | Additive; zero DROP/DELETE statements |
| PowerShell parser | All 11 scripts passed |
| Test cleanup | Zero recap_test_* databases, project testhosts or preparation temporary folders remained |
| Original hosts | API 30980 and worker 70964 remained running |

Total **186 passed, 0 failed, 0 skipped**. Final TRX: [73 integration tests](../../TestResults/preparation/preparation_final_net10.0_20261013091323.trx), [113 unit tests](../../TestResults/preparation/preparation_final_net10.0_20261013091326.trx). [Reviewed Up SQL](../../artifacts/phase2-verification/preparation-migrations.sql).

Final separate-host evidence: import `a5dc4e1f-018e-4a7f-a70c-1f04df0a9919`, validation `c07fe89c-2e38-4835-bb6c-a73a79a64dc4`, job `9d81e895-8927-4fb4-9d30-5c1b686a4c96`. Actual original-source/PNG checks passed, operator approval and created/validation-passed/approved audit events were recorded, then a video selection change returned EffectiveStatus=Stale/NeedsValidation=true. The smoke's explicit no-audio choice was supported; real WAV checking was separately exercised in integration tests. Source bytes/write timestamps and import-specific rights were preserved. IDs belonged to a removed disposable database.

The initial preparation-test build hit one xUnit analyzer error for filtering before Assert.Single; that assertion was corrected. Focused checks then passed 8 unit and 8 integration tests. An additional race test and extended host smoke preceded a passing 186-test acceptance run. Historical binding fields were then added to the new migration/model and asserted in tests; the final rebuilt 186-test run passed again. No failed/intermediate check is counted as passing verification.

## Changed areas and local activation

Media Application adds primitive preparation source/rights/track/choice and held-source/transaction contracts; Media-owned Infrastructure implements its reads/locks. VideoProcessing Domain/Application adds canonical binding/report/review/audit invariants, combined readiness/history and validation/check/decision/executor ports. Infrastructure adds real held-handle source/WAV/PNG verification, PostgreSQL snapshot/request/report/review/audit storage, import publication synchronization, model/readiness and additive migration/snapshot. Existing scene/audio/frame completions now share the import-level preparation lock. Jobs/worker add reserved internal validation execution; API adds protected overview/validation/report/decision/audit routes. Added 8 unit and 9 real integration cases, and extended the existing separate-host frame smoke to cover preparation review. Existing migration-count assertions expect nine. README/API/architecture/domain/rights/module/database/deployment/implementation/testing/roadmap/fixture docs and ADR 0011 were updated.

Application database and original hosts remain unchanged. Activate explicitly by applying pending migrations and rebuilding/restarting both hosts with consistent trusted Media/Audio/Frames roots. API needs source/artifact read access for fresh approval checks; worker uses the same roots for validation/generation. Preparation__CheckTimeoutSeconds defaults to 3600. No new provider or tool is required beyond existing preparation generation dependencies. PostgreSQL remains running; isolated binaries/SQL/TRX remain under artifacts/TestResults. No production service installation/deployment was performed.

## Limitations and next work

Overview is metadata plus a last-check observation; it does not continuously hash or monitor files. EffectiveStatus must be used for current approval because stored historical decision status can differ after a binding change. Any new completed validation invalidates prior decisions until a new explicit command. Fresh approval check failure blocks approval and requests a validation job to durably record issues; it does not invent a report. Actors remain caller assertions under shared-key access and rights remain Unreviewed. Approval grants no publishing authorization or later-phase job.

Full hash passes and fresh synchronous approval can be slow, and up to 302 verified file/source handles remain held through commit. Trusted shared local storage/identities, immutable completed metadata and synchronized clocks remain assumptions. Ordinary Windows sharing protects checked files, not hostile privileged/distributed mutation. There is no background validation monitor, global retention/quota or production role/authentication service.

Next work: separately scoped Phase 2 activation/acceptance rehearsal with configured local roots and protected environment. Other preprocessing and Phase 3 ASR/AI remain separately authorized. Burmese TTS, recap rendering, UI, publishing and rights approval/revocation are absent; this slice does not claim all Phase 2 capabilities are complete.
