# ADR 0012: Explicit local Phase 2 activation and rehearsal

Status: Accepted, 2026-10-13.

The implementation previously ran only in disposable verification databases. The application database and original console hosts still exposed Phase 1. Activate the confirmed Phase 2 code explicitly, preserving the existing database, protected credentials and original media.

Publish API and worker into a new `.local/runtime/releases/<release>` directory with a SHA-256 file manifest. Keep old deployed binaries and DPAPI credentials in a restricted backup directory. Verify a custom-format PostgreSQL dump through an actual disposable database restore before stopping idle hosts. Capture a final quiesced dump, apply only the reviewed additive Up migrations, then start the new release with a shared nonsecret profile and environment-injected credentials. Startup never migrates the database.

The local profile selects dedicated rehearsal media and artifact roots, actual FFprobe/FFmpeg/Python, disabled discovery, one-second polling and a fifteen-second lease. Short leases permit bounded crash-recovery rehearsal while retaining the existing heartbeat/fencing implementation. This is a local console deployment, not Windows Service installation or production deployment. Scripts start hidden processes and identify them by executable path plus process start time before stopping them.

Acceptance uses copies of repository-owned synthetic media, real authenticated API calls and independent WAV spectrum/decoded PNG checks. It exercises current approval, running cancellation, abrupt worker loss and lease recovery, selection/validation staleness, historical downloads, duplicate asset reuse, and unchanged original bytes/write timestamps. Receipts retain live identifiers and reports; database/artifact history remains available for inspection.

The additive schema permits retaining the old binaries as a rollback resource. Returning to old code requires quiescing Phase 2 jobs; the old worker cannot execute them. Database rollback is a separate destructive restore that loses post-backup records and must never run automatically. Back up later changes before a separately authorized restore. Preserve the corresponding artifact storage and protected credential identity.

No module boundaries or domain behavior change. Technical preparation approval remains separate from rights/publication authorization. Phase 3, ASR/AI, Burmese voice, rendering, publishing, remote security and service management are outside this activation.
