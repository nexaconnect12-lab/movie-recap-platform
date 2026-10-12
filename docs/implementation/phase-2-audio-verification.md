# Phase 2 audio verification — 2026-10-13 (Asia/Singapore)

Scope: separately proposed and confirmed selected-audio extraction/artifact slice only. Existing solution/instructions/docs were inspected; user confirmation authorized existing-file edits. Original media/import rights/application database/hosts were preserved. No later phase was implemented or simulated.

## Environment and exact commands

.NET SDK 10.0.400/net10.0, existing real local PostgreSQL, actual FFmpeg/FFprobe 9.0.2 from the existing workspace bundle and the previously pinned real scene Python environment. No new system-wide tool/provider/service installation or paid service was needed. Existing owned multi-video/multi-audio sine fixtures were reused without overwriting inputs. Protected local credentials were injected without printing secrets. SDK artifact isolation avoided replacing loaded original host binaries; tests used uniquely named databases, temporary libraries/artifact roots and loopback ports.

```powershell
. ./scripts/Set-AudioVerificationEnvironment.ps1
dotnet build MovieRecapPlatform.slnx -m:1
dotnet test MovieRecapPlatform.slnx -m:1 --no-build --logger 'trx;LogFilePrefix=audio_final' --results-directory TestResults/audio
dotnet ef migrations has-pending-model-changes --project src/Infrastructure --no-build
dotnet ef migrations script --project src/Infrastructure --no-build --idempotent --output artifacts/phase2-verification/audio-migrations.sql
```

TEST_POSTGRES, TEST_FFPROBE, TEST_FFMPEG and TEST_SCENE_PYTHON were populated. Earlier generation used `dotnet ef migrations add VideoAudioExtraction --project src/Infrastructure --no-build`; the generated migration is `20261012212737_VideoAudioExtraction`. Tests apply migrations only to their disposable databases. Neither host migrates on startup, and no application database update/restart was performed.

## Results

| Check | Result |
|---|---|
| Required solution build | Passed, 0 warnings, 0 errors |
| Unit/domain/architecture/adapter tests | 93 passed, 0 failed, 0 skipped |
| Real PostgreSQL/media/API/separate-host tests | 55 passed, 0 failed, 0 skipped |
| Selected nondefault stereo audio extraction | Real stream 3 (880 Hz) decoded to 16000 mono samples, 32044 WAV bytes |
| Independent output validation | Real FFprobe: one pcm_s16le audio stream, mono, 16 kHz, 16000 duration samples |
| Actual track verification | Output 880 Hz magnitude exceeds unchosen 440 Hz by required ratio |
| Concurrent replay / changed-parameter conflicts | One bound operation/job; conflicting replay rejected |
| Selection change / historical artifact | Stale completion rejected; completed old artifact remains downloadable/noncurrent |
| Source mutation / partial output / output cap | Rejected without partial publication; original bytes/timestamp preserved after normal execution |
| Post-promotion audit fault | Database reference/job success/audit rolled back; unreferenced WAV removed |
| Crash-window orphan / active lock / recovery | Orphan reconciled; active/referenced attempts preserved; recovered lease publishes its own token |
| Pause/cancel/expiry and expiry during promotion | Fenced publication; no success reference/audit from invalid lease |
| Subprocess timeout/output/cancellation/corrupt exit | Stable errors; owned process termination passed |
| Artifact integrity / source-root overlap / junctions | Truncated/changed artifacts and unsafe roots rejected; drive-root overlap edge covered |
| Prior-schema upgrade | Prior import/rights/inventory/choice and queued scene operation preserved; zero invented audio |
| Authenticated API / download / cleanup | Passed; unfinished download conflicts, generic audio job creation rejected |
| Separate API + worker audio smoke | Real extraction/download/checksum/audit/history/source preservation passed |
| Existing import/discovery/stream/scene smokes | Passed in same full suite |
| Pending EF model changes | None |
| Generated idempotent Up SQL | Additive, zero DROP/DELETE statements; new Up has no prior-data mutation |
| PowerShell parser | All 10 scripts passed |
| Test cleanup | Zero recap_test_* databases, project testhost processes or owned audio temporary folders remained |
| Original hosts | API 30980 and worker 70964 remained running |

Total **148 passed, 0 failed, 0 skipped**. Final TRX: [55 integration tests](../../TestResults/audio/audio_final_net10.0_20261013054059.trx), [93 unit tests](../../TestResults/audio/audio_final_net10.0_20261013054101.trx). [Reviewed Up SQL](../../artifacts/phase2-verification/audio-migrations.sql).

Final smoke evidence: extraction `d2f6ae0a-e137-44ab-87c6-6978b35a0654`, job `624a5ba1-e266-4450-9b8c-410831674d67`, chosen stream 3, actual FFmpeg 9.0.2, output SHA-256 `e3a394d916c7a163f5e71572ef4307fa145295150e71414f9de92a3cb51d0072`. Changing the choice to stream 2 kept the prior success/download as history. Original source bytes/write time were unchanged; no temporary PCM remained. These IDs belonged to a removed test database.

Initial audio-only verification passed 7/8 integration and 10/11 unit cases. One API test worker clock preceded system-time job availability, so claim returned null; the fixture clock was advanced before claiming. One Windows link test required an unavailable symbolic-link privilege; it now uses an actual junction, consistent with existing link tests. The first full acceptance then passed all 148. Final path review identified a drive-root separator edge in overlap detection; the implementation and regression assertion were corrected, rebuilt and the complete 148-test suite passed again. Initial failed TRX files remain as historical evidence and are not passing verification.

## Changed areas and local state

VideoProcessing Domain/Application adds source-bound extraction, fixed PCM profile/sample/artifact invariants, requests/history/download/execution and artifact/extractor/store ports. Media adds selected-audio Application contracts and Media-owned access/transaction adapter. Jobs adds internal audio kind/reserved namespace and a locked lease-validation contract before side-effect publication. Infrastructure implements real bounded FFmpeg extraction, canonical WAV validation, trusted owned filesystem staging/promotion/cleanup, PostgreSQL persistence, readiness/model/migration/snapshot. API adds authenticated request/list/read/artifact/cleanup routes; worker dispatch uses existing heartbeat/lifecycle/error handling. Added 11 unit cases, 8 PostgreSQL cases and one separate-host smoke; reused existing owned fixtures; migration-count assertions now expect seven. README, API/database/deployment/architecture/implementation/testing/roadmap/fixture docs and ADR 0009 were updated.

Application database/hosts remain at their prior state; activation requires applying migrations and rebuilding/restarting with matching Media roots, actual FFmpeg and a separate trusted Audio__Root shared by both hosts. Isolated binaries/SQL/TRX remain under artifacts/TestResults; no application artifact root was created by the verification helper. PostgreSQL remains running. Test outputs/databases/processes were cleaned, and user media was not altered.

## Limitations and next work

Fixed downmix/resampling is a derived analysis profile, not original-channel/codec preservation. Output duration is actual PCM samples/16000, without exported original PTS/gap/video alignment. Full source/output/download hashes are costly; progress is coarse, and final validation holds job/selection locks. Limits cap each attempt's bytes/runtime/diagnostics, not total disk usage or OS decoder memory. Trusted local roots/service identities and shared artifact visibility remain deployment assumptions. Filesystem promotion and PostgreSQL commit are not a distributed transaction; reference-aware cleanup covers ordinary process crash/rollback windows, while power-loss durability depends on deployment storage. Failed/abrupt cleanup can require a subsequent retry or explicit scoped cleanup. Ownership/lock markers and successful history remain; no global janitor/retention/deletion policy is implemented. Backups must preserve references and files together.

Rights and operator identity remain caller assertions under shared-key access; rights stay Unreviewed and no publication gate/service is delivered. No SCM service installation/production deployment was tested. Next proposed Phase 2 slice is source-bound scene thumbnails/frame sampling using managed artifacts with explicit timestamp mapping, subject to separate confirmation. ASR/AI, Burmese TTS, subtitle extraction, rendering, UI and publishing remain absent.
