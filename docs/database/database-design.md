# Database design
Implemented schemas: jobs and media. EF generated InitialJobs, AuditOrdering, ManualMediaImport and MediaDiscoveryInbox migrations and model snapshot are in src/Infrastructure/Persistence/Migrations. EF migration history is in the default public schema.

ProcessingJobs: UUID primary key; Kind (64), unique IdempotencyKey (128), string Status (32), Attempts, MaxAttempts, Progress, CreatedAt/UpdatedAt/AvailableAt (timestamptz), nullable WorkerId (128), LeaseToken/LeaseExpiresAt, ErrorCode (128), Version UUID concurrency token. Indexes on Status+AvailableAt and LeaseExpiresAt. Checks constrain progress and retry budget.

AuditLogs: UUID key, JobId restrictive foreign key, action (64), resulting status (32), attempt and UTC timestamp; index JobId+At. A database-generated unique Sequence orders events deterministically even when timestamps match. No delete API. Audit retention/export is future administration work.

Claim transaction: SELECT candidate ORDER BY availability/creation LIMIT 1 FOR UPDATE SKIP LOCKED. Save lease/state and audit before committing. Normal state changes lock the row and verify the supplied version; worker mutations verify the execution token and expiry. Unique-key collisions during creation return the original matching job; conflicting parameters yield 409. A single EF SaveChanges transaction saves job creation and audit.

Run migrations explicitly before starting hosts. Neither host migrates on startup, avoiding competing schema changes and excessive runtime privileges. Use a migration role with DDL and a runtime role with schema USAGE and table SELECT/INSERT/UPDATE. Restrict runtime DELETE. Local compose's recap role is a development-only database administrator.

The master prompt's remaining entities are designed in module-catalog.md and deferred to the phases that implement their behaviors. No speculative empty tables are added. Never persist plaintext access/refresh tokens. Backups, retention, migration rollback testing and least-privilege role provisioning are production work.

Phase 2 additive migration `20261012121403_ManualMediaImport` creates the `media` schema:
- MediaAssets: UUID identity, unique SHA-256, positive byte size, creation UTC, container/duration/video codec/dimensions/fps and optional audio metadata.
- MovieImports: UUID, unique request key and JobId, folder alias/relative path, source/requester, retry budget/created UTC, nullable AssetId/CompletedAt and duplicate flag. Completion fields have a consistency constraint; AssetId is a restrictive same-module FK.
- MediaRights: one restrictive same-module import FK; owner, authorization assertion, license evidence, permitted uses/platforms, attribution and optional expiry. Rights do not transfer between duplicate imports. No publishing/token fields exist.

No FK/navigation crosses to Jobs. An Infrastructure Jobs transaction adapter implements Application contracts on the same database transaction for creation/fenced completion/audit. Equal request keys and hashes use transaction advisory locks and unique indexes. Status queries use repeatable read. Readiness now verifies all four implemented table sets (jobs plus three media tables). Existing migrations remain intact; migration Up is additive. Down drops only the three media tables and is destructive to Phase 2 records, so it is not automatically applied to the user's database.

MediaDiscoveryInbox adds media.MediaDiscoveries and nullable MovieImports.ExpectedFingerprint. Discoveries store folder/path, compact SHA-256 path key, metadata fingerprint, byte size, state, availability/error, timestamps, Version, current-revision marker and optional local import/submission references. A filtered unique (Folder,PathKey) index where IsCurrent enforces one current location revision; historical rows remain. Submission-consistency/fingerprint checks and a restrictive same-module ImportId FK protect state. Reconciliation takes per-folder advisory and candidate-row locks; older observations cannot overwrite newer ones. Shared import-row locks make idempotent replay views consistent while completion is in progress. Readiness also queries discovery storage. Up adds a nullable column/table/indexes; Down removes those additions and is destructive to discovery history.

