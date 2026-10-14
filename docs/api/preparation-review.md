# Phase 2 preparation view, validation and operator review

The selected-subtitle slice extends this API after `VideoSubtitleExtraction` activation. Snapshots include current SubRip extraction/cues; a selected subtitle requires that artifact, while explicit no-subtitle remains valid. Real SRT checks join validation/fresh approval, new subtitle output stales decisions, and history includes extraction jobs. Binding schema version 2 records subtitle identity; historical version-1 fingerprints and no-subtitle compatibility are preserved. See [subtitle API/configuration](subtitle-extraction.md). Earlier artifact lists below describe the original preparation slice.

Apply additive `VideoPreparationReview`, rebuild/restart both hosts and keep identical trusted Media roots and Audio/Frames artifact roots. Validation uses the existing source/WAV/PNG verifiers. No extra provider, Python package or external tool is added; generating the artifacts still needs the previously configured real tools. Hosts never migrate automatically.

`Preparation__CheckTimeoutSeconds` defaults to 3600, allowed 1–86400. It bounds filesystem verification for worker checks and fresh approval checks. Worker lease heartbeat/cancellation/attempt budgets remain unchanged. All endpoints require `X-Api-Key`; requester identities are assertions under existing shared-key access, not authenticated individual users.

GET `/api/media/imports/{importId}/preparation?limit=5` returns:

- `snapshot`: Media source/rights assertions, complete stream inventory and current explicit choices; selected completed scene/ranges, optional audio, frame batch/samples, deterministic `snapshotHash`, `isReady` and readiness issues.
- `review`: stored review/version/last validation and historical operator decision, or null before the first validation/rejection.
- `effectiveStatus`: Pending/Approved/Rejected/Stale for the current snapshot. Use this field rather than interpreting a historical stored decision as current approval.
- `needsValidation`, latest validation/report/job, and recent scene/audio/frame/validation operations (limit clamped 1–20 per kind).

The bundle uses the newest completed current-choice scene analysis, the newest completed frame batch for that analysis, and the newest completed matching audio extraction when audio is selected. Ordering is completion time then UUID for deterministic ties. Queued/failed attempts are history and do not replace completed content. No-audio is valid only when the operator's current choice has audioIndex=null. Missing scenes, frames or selected audio make the bundle not ready; no fallback guesses are made. GET performs metadata reads, not filesystem hashing, and returns 409 if selection/source revision changes during assembly. Filesystem availability remains the last recorded validation observation, not a continuous monitor.

POST `/api/media/imports/{importId}/preparation/validations`:

```json
{
  "idempotencyKey": "preparation-check-001",
  "snapshotHash": "HASH-FROM-CURRENT-OVERVIEW",
  "requestedBy": "local-operator",
  "maxAttempts": 3
}
```

Validation requires a ready current snapshot. Keys accept 1–128 ASCII letters/digits/`._:-`, actor is required/max 256, attempts 1–5 and hash must be 64 lowercase hex characters. Creation atomically saves a bound validation request, its job/audit and the review's initial record/audit when needed. Matching import-scoped key/hash/actor/budget replays the original operation even after preparation changes; changed parameters conflict. Response 201/Location points to GET `/api/video/preparation-validations/{id}`. Internal `video.preparation-validation` jobs and `preparation-validation:` keys cannot be created through generic Jobs POST.

Poll the validation's shared job or use existing [pause/resume/cancel/retry/audit routes](jobs.md). The worker opens/hashes the original against its imported identity, validates/hashes the WAV if selected, and validates every referenced PNG. Verified read handles remain held through commit; original source hashing runs twice. Issues have bounded stable target/code pairs (`source`, `audio`, `frame-N`), without raw diagnostics/paths. Validation records retain exact import/asset/inventory/selection/index/source/scene/audio/frame bindings and fingerprint for historical inspection.

**Job Succeeded means the check completed; `validation.passed` tells whether integrity passed.** Missing/corrupt artifacts or changed source bytes can produce a completed check with passed=false/issues. Transient executor/database failure or stale lease/bundle cannot publish a report and follows normal job failure/retry behavior. Report/review version/state/audit/job completion commit together after lease and current-bundle checks. Changes during checking invalidate the request; retries remain bound to its original hash. Use a new key/current hash to validate changed preparation.

POST `/api/media/imports/{importId}/preparation/decision`:

```json
{
  "version": "CURRENT-REVIEW-VERSION-UUID",
  "snapshotHash": "HASH-FROM-CURRENT-OVERVIEW",
  "decision": "Approved",
  "requestedBy": "local-operator",
  "notes": "Reviewed the prepared scenes, audio and thumbnails"
}
```

Only Approved/Rejected is accepted. Notes are required as a field, max 2048; rejection requires nonblank notes. Approval requires a ready current snapshot and a successful latest matching validation. It reopens/rechecks source and all files, retains verified handles, then rechecks hash/version/latest validation under transaction locks before recording the decision/audit. Concurrent commands using the same version allow one commit; stale versions or snapshot hashes return 409. Fresh file-check failure prevents approval; queue validation to record those issues. Rejection works for incomplete preparation without reading files. If no review record exists, use the all-zero UUID as version for the first rejection; subsequent commands use returned version.

GET `/api/media/imports/{importId}/preparation/audit?limit=50` returns ordered historical events (latest first; clamped 1–100), including actor/notes/before-after versions/exact binding/hash/validation identity. Review decisions do not mutate files, sources, stream choices or import rights.

A different selection/source binding or selected completed artifact set makes the previous decision effectively Stale. Every subsequent completed validation also marks an existing decision Stale, including a successful recheck; an operator must make a new versioned decision. History is retained. Staleness from changed metadata is derived at read time; prior stored decisions/audits remain historical. No background filesystem watcher/periodic validation is added. Missing bytes without a new check cannot automatically update the last observation.

Preparation approval is a technical operator checkpoint. Import rights remain Unreviewed and unchanged; approval grants no publication authorization, performs no AI/ASR/TTS/rendering and creates no later-phase jobs. Limits include potentially long HTTP approval checks/full hash passes, trusted roots/identities, up to 300 image read handles plus audio/source, synchronized clocks and local filesystem consistency rather than a distributed storage guarantee.
