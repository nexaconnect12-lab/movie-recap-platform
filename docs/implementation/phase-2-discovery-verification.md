# Phase 2 discovery verification — 2026-10-12 (Asia/Singapore)
This is the original import/discovery acceptance snapshot. Latest combined acceptance including stream inspection/selection is in [stream verification](phase-2-streams-verification.md).

Scope: confirmed folder-monitoring and discovery-inbox slice. Existing solution/instructions were inspected, changes were proposed, and the user explicitly authorized them before existing files were modified. Original media, existing hosts and the application database were preserved.

## Commands and environment

.NET SDK 10.0.400/net10.0; real local PostgreSQL; actual FFprobe 9.0.2 from the existing checksum-verified workspace bundle. No new provider/service installation was added. Protected credentials were injected through the existing environment helper without printing them. SDK artifact isolation preserved normal Debug outputs used by the original hosts.

```powershell
. ./scripts/Set-Phase2VerificationEnvironment.ps1
docker compose -f infrastructure/compose.yml up -d --wait
dotnet build MovieRecapPlatform.slnx -m:1
dotnet test MovieRecapPlatform.slnx -m:1 --no-build --logger 'trx;LogFilePrefix=discovery_acceptance' --results-directory TestResults/discovery
dotnet ef migrations has-pending-model-changes --project src/Infrastructure --no-build
dotnet ef migrations script --project src/Infrastructure --no-build --idempotent --output artifacts/phase2-verification/discovery-migrations.sql
```

TEST_POSTGRES and TEST_FFPROBE were populated. PostgreSQL tests created/migrated/dropped only their generated databases. Both two-host smokes started their own API and worker using disposable databases/loopback ports, drained outputs and cleaned their owned processes/media folders.

## Acceptance evidence

Final acceptance results after the replay-row-lock change:

| Check | Result |
|---|---|
| `dotnet build MovieRecapPlatform.slnx -m:1` with isolated SDK artifacts | Passed; 0 warnings, 0 errors |
| Unit / architecture / adapter tests | 54 passed, 0 failed, 0 skipped |
| Real PostgreSQL / FFprobe / API / separate-host tests | 25 passed, 0 failed, 0 skipped |
| Existing manual-import upgrade + repeated migration | Passed; source/rights/job/audit records preserved, nullable fingerprint remains null |
| Transactional fault injection and cancellation | Passed; no partial candidate submission/import/job/rights/audit |
| Concurrent scans/submissions and versioned replay | Passed |
| Separate-host discovery/partial-copy/restart smoke | Passed with actual FFprobe and preserved original bytes; no automatic jobs |
| Existing separate-host manual import/duplicate/corrupt smoke | Passed |
| EF pending model changes | None |
| Idempotent migration Up SQL review | Additive; no DROP/DELETE statements |
| PowerShell parsing | All 8 supplied scripts passed |
| Cleanup | No `recap_test_*` databases or project testhost processes remained |
| Original hosts | API process 30980 and worker process 70964 remained running |

Total: **79 passed, 0 failed, 0 skipped**. Final TRX evidence: [integration/media/hosts](../../TestResults/discovery/discovery_acceptance_net10.0_20261012212743.trx), [unit/architecture/adapters](../../TestResults/discovery/discovery_acceptance_net10.0_20261012212745.trx). Reviewed SQL: [idempotent migration Up script](../../artifacts/phase2-verification/discovery-migrations.sql).

Discovery smoke evidence: partial-copy candidate `dc42bc9f-5d81-4c20-a725-53c5858c88ac`, successful import `baac24f5-119f-41fc-9c53-efcb3841b0af`, missed-event candidate recovered after restart `9f5428e3-1362-4bc0-8c89-b0cb20a82280`. These identities belonged to a disposable test database removed after the check.

Checks cover discovery stability/readability/version/history, empty/busy/cancelled/missing/capped scans, safe recursion/junction exclusion, changed-source fingerprint rejection, concurrent scans/submissions, stale/partial reconciliation, idempotent replay, explicit rights/API access validation, trigger-injected transaction rollback, actual FFprobe execution and migration upgrade preserving old manual import/rights data. Separate-host discovery smoke verified a locked partial copy remained unready, no automatic jobs appeared, explicit rights submission produced a real successful import, original bytes remained unchanged, and a worker restart discovered a file created with no live watcher. Original manual-import/fencing/probe tests remained in the full suite.

The SQL Up script adds MediaDiscoveries and nullable ExpectedFingerprint; it does not remove existing tables/data. Down is destructive to discovery history and was not applied to the application database. No pending EF model changes are expected after the generated migration.

Initial checks hit sandbox NuGet audit network restrictions; authorized network-enabled restore/build resolved them without suppressing audit. Two new test compilation issues (Assert.Single analyzer usage and a captured port declaration) were corrected before the first passing full run. A final consistency review added a shared import-row lock for replay reads, followed by the final acceptance suite. No failing check is counted as verification.

## Changed areas

- Media Domain/Application: discovery state/revision model, observation/scan/store contracts, explicit submission service, expected import fingerprint and reserved discovery key namespace.
- Infrastructure: safe scanner/fingerprints/options, reconciliation/submission store, shared import transaction helper/replay lock, model mapping/readiness, additive MediaDiscoveryInbox migration and snapshot.
- Hosts: worker monitor with coalesced filesystem hints and periodic rescans, opt-in configuration, authenticated inbox list/detail/dismiss/import endpoints.
- Tests: 7 added unit/adapter tests; 8 added database/media/API/upgrade tests; new two-host partial-copy/restart smoke; previous migration-count assertion updated for the new additive migration.
- README, architecture/domain/rights/module/database/deployment/API docs, implementation plan/roadmap/testing/current evidence and ADR 0006.

## Local state, limits and next work

The application database is not automatically upgraded while its existing hosts run. Generated migrations were applied to disposable databases, including an upgrade from the previous manual-import migration. Existing host processes are preserved; test processes and generated databases are cleaned. PostgreSQL remains available. Verification binaries, SQL and TRX evidence remain under ignored artifacts/TestResults. No user media was copied/renamed/deleted by monitoring or imports; smoke copied only owned synthetic test fixtures into disposable folders.

Activation: apply migrations, rebuild/restart the hosts with matching trusted media roots and a real FFprobe path, and set Discovery__Enabled=true in the worker. Monitoring defaults to disabled. See [configuration/API usage](../api/discovery-inbox.md).

Limits: metadata/readability-based discovery, trusted stable folders, reference-only media storage, periodic latency and bounded scans, historical rows without retention/cursor pagination UI. A privileged writer preserving location/size/write/creation metadata is outside the fingerprint guarantee; byte hashing remains in the import job. Entries beyond the scan cap require narrowing folders or raising the cap. Actual OS buffer-overflow injection and administrative Windows Service lifecycle are not claimed. Rights remain caller assertions and Unreviewed; discovery does not authorize publication or automatically import.

Next Phase 2 slice: fuller stream inspection/selection and PySceneDetect scene detection with owned fixtures. No stream preprocessing, scenes, AI, Burmese TTS, rendering, dashboard, publishing or later-phase functionality is implemented here.

Historical slice evidence. Latest full-suite verification: [Phase 2 scenes](phase-2-scenes-verification.md).

Historical slice evidence. Latest full-suite verification: [Phase 2 audio](phase-2-audio-verification.md).
