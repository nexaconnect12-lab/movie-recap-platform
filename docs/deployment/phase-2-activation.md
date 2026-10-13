# Activated local Phase 2 release

The confirmed local activation uses `.local/runtime/active.json` for nonsecret release/tool/root paths and `.local/credentials.xml` for Windows DPAPI-protected credentials. Both hosts read the same environment. The application database is `recap` in `movie-recap-phase1-postgres-1`, bound to loopback port 54329; API is loopback port 5080. Hosts never migrate automatically.

From Windows PowerShell 7 in the workspace:

```powershell
. ./scripts/Set-ActivatedEnvironment.ps1
Invoke-WebRequest http://127.0.0.1:5080/health/ready
./scripts/Stop-ActivatedHosts.ps1
./scripts/Start-ActivatedHosts.ps1
```

`-HostKind Api` or `-HostKind Worker` targets one host. Stop verifies the recorded PID, executable and UTC start time before acting. Start refuses a still-running tracked process. Standard output/error and worker structured logs live under `.local/logs`; tracked process identities live under `.local/runtime`. Processes are hidden local console hosts; they do not automatically restart after reboot. Docker is retained from local foundation setup. No service is installed.

`Set-ActivatedEnvironment.ps1` loads existing protected credentials without printing them, validates workspace-contained paths and rejects reparse points. Profile paths require trusted local access and consistent configuration on both hosts. This helper deliberately supports this workspace-local rehearsal deployment; arbitrary production tool/storage locations require separately scoped configuration. Media alias `rehearsal` points to `.local/media/rehearsal`; Audio/Frames use `.local/artifacts` with distinct namespaces. Discovery is disabled. Polling is one second and lease is fifteen seconds for recovery acceptance. Shared-key access remains local development authentication.

To repeat the acceptance workflow against these hosts:

```powershell
./scripts/Rehearse-Phase2.ps1
```

This command creates a uniquely named owned-fixture copy, imports with explicit local-test rights assertions, selects video 1/audio 3/subtitle 5, generates scenes/WAV/three PNGs, validates and approves. It intentionally stops/restarts the tracked worker during a running validation to test lease recovery. It also checks running cancellation, stale approvals, historical downloads, duplicates and source preservation, then regenerates/reapproves the primary preparation. Run when no other operators are using the worker. `-SkipLifecycle` avoids cancellation/crash testing and is recorded as skipped in its receipt; it does not count as full activation acceptance. The Python artifact verifier uses the existing scene environment's NumPy/OpenCV and checks actual 880 Hz PCM plus blue decoded frames and black padding.

Receipts and independently downloaded artifacts live under `.local/runtime/receipts/<run>`. The script leaves media, referenced artifacts and database records intact. Repeating creates more durable history; there is no global retention service. If interrupted, inspect jobs/receipts/logs and preserve evidence; never rerun against a user file or remove referenced output. Stopping a worker is abrupt; shutdown/lease expiration and cancellation are fenced by the application, not simulated by the script.

## Backup and rollback

Activation backups are under `.local/backups/activation-20261013-01`, restricted to the current Windows identity, SYSTEM and Administrators. They contain previous API/worker binaries, encrypted credentials, `recap.dump`, `quiesced.dump` and a manifest. Both dumps were restored into separate disposable databases and verified at two migrations/two jobs/six audit rows; both disposable databases were dropped. The baseline contained no media or artifact references. The final quiesced dump captures the pre-migration boundary. No originals are deleted.

The new release is `.local/runtime/releases/activation-20261013-01`; retain its manifest and active profile. Before later activation, inventory active jobs and referenced storage, back up the database and all referenced artifacts, restore-verify the backup, publish into a new release directory, quiesce hosts, apply reviewed migrations and verify health/acceptance before resuming operators.

For code rollback, stop the tracked new hosts, ensure no Phase 2 jobs can be claimed by an old worker, then start the retained previous executables with the protected local environment and their own content roots. The seven new migrations are additive; do not automatically apply Down migrations. Retain the upgraded database and all artifacts for inspection.

Database restore is separately authorized destructive work: stop writers, back up current database/artifacts, verify target database identity and restore the selected custom dump with `pg_restore` into an explicitly chosen recovery database. Validate migrations/counts and corresponding artifact references before changing the connection. Do not drop or overwrite the live database merely to test rollback. Post-backup changes cannot be recovered from the old dump; DPAPI credentials require the original Windows identity/protected recovery arrangement. This rehearsal verifies dump restoration, not a destructive live rollback or disaster recovery on another machine.
