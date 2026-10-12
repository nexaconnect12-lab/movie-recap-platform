# Phase 1 verification — 2026-10-12 (Asia/Singapore)
This is historical Phase 1 evidence. Manual-import evidence is in [Phase 2 verification](phase-2-verification.md), import/discovery evidence in [discovery verification](phase-2-discovery-verification.md), and latest combined acceptance in [stream verification](phase-2-streams-verification.md).
Final state: foundation implemented and verified. Existing user files were not overwritten. Source specification preserved in ../requirements/master-prompt.md. Canonical project location: D:\DevOps\movie-recap-platform (confirmed after the user requested this path).

## Observed results
| Check | Result |
|---|---|
| .NET SDK | 10.0.400; target net10.0 |
| Solution build | Passed; 0 warnings, 0 errors |
| Unit + architecture tests | 20 passed, 0 failed, 0 skipped |
| Real PostgreSQL/API integration tests | 4 passed, 0 failed, 0 skipped |
| EF InitialJobs migration | Generated and applied to fresh PostgreSQL |
| EF AuditOrdering migration | Generated and applied; final tests use both migrations |
| Separate API + worker smoke | Passed; job 10cf032d-41ab-4bba-ba8e-bf85f45d7e60 Succeeded, attempt 1, progress 100, created/started/succeeded audit |
| Self-contained win-x64 Release worker publish | Passed after final source changes |
| PowerShell script parsing | Passed for all supplied service/local scripts |
| Administrative SCM install/start/stop/recovery | Not executed; deployment acceptance remains |

Final TRX evidence:
- [Integration tests](../../TestResults/verified_net10.0_20261012194648.trx)
- [Unit tests](../../TestResults/verified_net10.0_20261012194649.trx)

The separate-host smoke preceded the final audit-ordering migration; final PostgreSQL/API tests then verified deterministic audit reads against the final schema. Generated worker publication was refreshed after that change.

## Commands exercised
From repository root, with local environment and workspace-isolated build caches configured:
```powershell
dotnet build MovieRecapPlatform.slnx -m:1 --no-restore
dotnet test MovieRecapPlatform.slnx -m:1 --no-build --logger 'trx;LogFilePrefix=verified' --results-directory TestResults
dotnet ef database update --project src/Infrastructure
dotnet publish src/Workers/ProcessingWorker -c Release -r win-x64 --self-contained true -o artifacts/worker -m:1 --no-restore
```
TEST_POSTGRES pointed to a dedicated local Docker PostgreSQL instance. Each integration test created and dropped only its own randomly named database.

NuGet/network and Docker engine access were blocked by the sandbox initially; permitted execution outside that sandbox completed all checks. Restore found a vulnerable OpenAPI.NET transitive dependency; a direct 2.7.5 patch pin resolved the advisory without suppressing audit warnings. An initial API test-host configuration failure was corrected; the final suite passed.

## Changed areas
- Solution/SDK/build configuration and seven project files.
- Jobs Domain/Application lifecycle, validator, view contracts and IJobStore.
- EF PostgreSQL context/store, health registration, two migrations and snapshot.
- API endpoints, key protection, validation, OpenAPI and problem responses.
- Windows-compatible processing worker, options, heartbeat, cancellation and compact rolling JSON logs.
- Unit, architectural and PostgreSQL/API integration tests.
- PostgreSQL compose; local DPAPI helpers; smoke test; four service lifecycle scripts.
- README, AGENTS.md, .openai instructions, architecture/domain/database/requirements/API/pipeline/deployment/testing/roadmap documentation and four ADRs.

## Local state left behind
API/worker smoke processes were stopped. The dedicated PostgreSQL container is stopped; its application volume remains with both migrations and the smoke job. Local helper credentials are encrypted in ignored .local/credentials.xml and match that database. Initialize-Local preserves them. Normal startup restarts the container; no password entry is required for this Windows user. Published worker binaries remain in artifacts/worker. No Windows Service was installed.

## Remaining work
No movie import, FFprobe/PySceneDetect, AI, recap, Burmese TTS, subtitles, rendering, dashboard, rights approval or publishing implementation exists. Future schemas are documented, not delivered. Quartz persistent scheduling is Phase 7. Stronger production authentication/authorization, protected production secrets, least-privilege database provisioning, worker fleet metrics, fault/performance testing and administrative service acceptance remain.

Recommended next task: Phase 2 manual import with rights metadata, stable-file checks, SHA-256 deduplication and FFprobe metadata, backed by owned fixture files and real integration tests. See roadmap.md.


Relocation verification: solution rebuilt successfully at D:\DevOps\movie-recap-platform with 0 warnings/errors; all 20 unit/architecture tests passed. PostgreSQL integration tests were not rerun for this path-only change.
