# Phase 2 stream verification — 2026-10-13 (Asia/Singapore)

Scope: confirmed stream inspection/selection slice only. Existing solution/instructions were inspected, proposed changes were confirmed, and original media/existing hosts/application database were preserved. No scene or later-phase processing was added.

## Environment and exact commands

.NET SDK 10.0.400/net10.0; existing real local PostgreSQL; actual FFprobe/FFmpeg 9.0.2 from the previously checksum-verified workspace bundle. New synthetic fixtures were created from lavfi video/audio plus an owned caption/image; no third-party movie/audio/artwork was used. No tool/provider/service was installed system-wide. Protected local credentials were injected through the existing helper without being printed. SDK artifact isolation avoids replacing binaries loaded by the original hosts.

```powershell
. ./scripts/Set-Phase2VerificationEnvironment.ps1
docker compose -f infrastructure/compose.yml up -d --wait
dotnet build MovieRecapPlatform.slnx -m:1
dotnet test MovieRecapPlatform.slnx -m:1 --no-build --logger 'trx;LogFilePrefix=streams_acceptance' --results-directory TestResults/streams
dotnet ef migrations has-pending-model-changes --project src/Infrastructure --no-build
dotnet ef migrations script --project src/Infrastructure --no-build --idempotent --output artifacts/phase2-verification/stream-migrations.sql
```

TEST_POSTGRES and TEST_FFPROBE were populated for all required tests. Integration databases, media copies, host processes and loopback ports are isolated/owned by each test. Every host smoke drains stdout/stderr and terminates only its started processes. Existing application hosts/database are not upgraded or restarted automatically.

## Results

Final acceptance results:

| Check | Result |
|---|---|
| `dotnet build MovieRecapPlatform.slnx -m:1` with isolated SDK artifacts | Passed; 0 warnings, 0 errors |
| Unit / architecture / adapter tests | 69 passed, 0 failed, 0 skipped |
| Real PostgreSQL / FFprobe / API / host tests | 36 passed, 0 failed, 0 skipped |
| Multi-video/audio/language/subtitle/cover/silent probes | Passed with actual FFprobe |
| Concurrent choices / version conflicts / audit rollback / cancellation | Passed on real PostgreSQL |
| Source byte change with unchanged size/write timestamp | Rejected without selection/audit changes |
| Legacy reinspection / idempotency / fencing / recovery | Passed with real hash/probe; no fabricated inventory |
| Additive upgrade from MediaDiscoveryInbox | Existing import/asset/rights preserved, initially Uninspected |
| Separate API + worker stream smoke | Import/selection/audit/reinspection passed; source bytes/write timestamp preserved |
| Existing manual/discovery host smokes | Passed in the same full suite |
| Pending EF model changes | None |
| Generated migration Up SQL review | Additive; no DROP/DELETE statements |
| PowerShell parsing | All 8 supplied scripts passed |
| Test cleanup | No `recap_test_*` databases or project testhost processes remained |
| Original hosts | API process 30980 and worker process 70964 remained running |

Total: **105 passed, 0 failed, 0 skipped**. Final TRX: [integration/media/API/hosts](../../TestResults/streams/streams_acceptance_net10.0_20261013044444.trx), [unit/architecture/adapters](../../TestResults/streams/streams_acceptance_net10.0_20261013044446.trx). Reviewed SQL: [idempotent Up script](../../artifacts/phase2-verification/stream-migrations.sql).

Stream smoke evidence: import `cc882034-c81d-4ddb-828e-8b6ae2b6c5d9`, chosen video 1/audio 3/subtitle 5, reinspection job `8f2af451-1da1-456f-9fd5-c51c2d918f80` succeeded, immutable inventory `934c28ed-5ce9-4c2a-93e0-2c08bac40868`. These identities belonged to a disposable test database removed after verification.

The first full run passed 69 unit/architecture/adapter tests and 35 of 36 integration tests, including the real stream host smoke. The remaining test exposed malformed request-body exceptions being mapped to 500 when the required videoIndex field was absent. The API handler was corrected to preserve client-request status and emit a generic body-error title; the build passed again before final acceptance. Intermediate failure evidence remains under TestResults/streams and is not counted as passing verification.

Coverage includes actual six-track MKV metadata (two videos, three audio tracks with English/Japanese/missing language, forced subtitle), cover-art exclusion and silent video; optional unknown duration/language preservation; malformed/duplicate indexes; explicit selection and category checks; shared inventory/independent choices on duplicate content; concurrency/version conflicts; source byte changes even with preserved size/write time; read locks/cancellation; trigger-injected audit rollback; real legacy reinspection/idempotency/fencing/pause/cancel/expiry/recovery; migration retaining old import/asset/rights and leaving streams explicitly Uninspected; authenticated API selection/inventory/audit/inspection; separate API/worker import/select/audit/reinspection with immutable inventory and preserved source bytes/write timestamp. Existing foundation/import/discovery tests remain in the full suite.

## Changed areas

- Media Domain/Application: full stream metadata/invariants, explicit selection state/use cases, inventory/source-verification/reinspection contracts and optional inventory in inspection results.
- Jobs: internal media.stream-inspection execution kind and reserved coordinator key namespace; public generic creation remains foundation-only.
- Infrastructure: detailed real FFprobe parsing, source verifier retaining a read-only handle, inventory/selection/audit/inspection persistence, shared fenced import completion initialization, readiness/model mapping and additive MediaStreamSelection migration/snapshot.
- Hosts: authenticated streams/selection/audit/reinspection endpoints, required videoIndex/body-error handling, real worker dispatch/error codes.
- Tests: 15 added unit/adapter cases, 10 added real media/database/API/upgrade cases and one separate-host stream smoke; owned media/caption/cover fixtures; previous migration-count assertion updated to five migrations.
- README, architecture/domain/module/rights/database/API/deployment/implementation/roadmap/testing docs and ADR 0007.

## Local state and limitations

The application database remains at its previous state while original hosts run. New additive migrations were applied to disposable test databases, including upgrade from MediaDiscoveryInbox preserving representative completed import/asset/rights records. Test processes and databases are cleaned. PostgreSQL remains running. Isolated binaries, generated SQL and TRX results remain under artifacts/TestResults; new owned inputs remain under tests/Fixtures. No user source media was altered.

Activation: apply migrations, rebuild/restart configured hosts with matching trusted roots/real FFprobe. Newly imported content gets real inventories; existing content remains Uninspected until an explicit reinspection job verifies the original source hash. Selection starts unselected; no default track/language guess is made. See [API/configuration](../api/media-streams.md).

Limits: metadata eligibility without full decoding/extraction, 256-stream cap and bounded FFprobe output, two full source hash passes for a selection request (final pass holds a database row lock), potentially long HTTP calls on large media, trusted stable root configuration/reference-only storage, and immutable inventories that reject contradictory probe output rather than silently replacing existing choices. Rights/operator identity remain caller assertions under shared-key access; all rights stay Unreviewed for publishing. No administrative Windows Service acceptance or production deployment was performed.

Next Phase 2 slice: PySceneDetect scene detection using the selected video via Media Application contracts, persisted time ranges and cancellation/recovery/owned fixtures. No audio/subtitle extraction, scene processing, AI, Burmese TTS, rendering, dashboard or publishing is implemented here.
