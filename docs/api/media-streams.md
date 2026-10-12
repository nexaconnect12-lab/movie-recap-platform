# Stream inspection and selection — Phase 2

Apply MediaStreamSelection before starting the new API/worker; readiness checks its five Media tables. Configure matching trusted Media folders and a real FFprobe executable as in [manual imports](media-imports.md). No new executable/provider is required. Original files remain in place and are never modified by inspection or selection.

All endpoints require X-Api-Key:

| Method / path | Result |
|---|---|
| GET /api/media/imports/{id}/streams | Stream inventory and that import's selection state, or 404 |
| PUT /api/media/imports/{id}/stream-selection | Source-verified, versioned selection update, or 404; invalid indexes 400; stale/uninspected 409 |
| GET /api/media/imports/{id}/stream-selection/audit | Ordered immutable selection events, or 404 |
| POST /api/media/imports/{id}/stream-inspections | Create/replay a real reinspection job, 201 and job Location, or 404; incomplete import/conflicting key 409 |

The streams view includes importId, assetId, status, inventoryVersion, inspectedAt, streams and selection. Status is NotImported when the import has no completed asset, Uninspected when no real full inventory has been persisted, and Inspected when the asset inventory exists. An old duplicate may have shared inventory but a null selection row; reinspect that import's own source to initialize it. New successful imports create an unselected row with a Version and null chosen indexes. Default/forced flags are metadata, not automatic choices.

Each stream uses the actual **global FFprobe index** (not video/audio-relative numbering). Fields: kind (Video/Audio/Subtitle/Other), reportedType, codec, language, optional durationSeconds, isDefault/isForced/isAttachedPicture, width/height/frameRate, channels/sampleRate and isSelectable. Missing/N/A durations remain null; they are not substituted with container duration. Missing/und language remains null. Inventory includes attachments/data/unknown categories but they are not selectable. Cover-art video is not selectable as the main video.

Select using the current selection Version from GET:

```json
{
  "version": "<current selection UUID>",
  "videoIndex": 0,
  "audioIndex": 2,
  "subtitleIndex": 5,
  "requestedBy": "local-operator"
}
```

videoIndex is explicitly required and must identify eligible non-cover video. audioIndex/subtitleIndex are optional; null means disable that track, including intentionally silent use of a video that has audio. Indices must have the requested category and eligible metadata. requestedBy is required, up to 256 characters, and is a caller assertion under shared-key access. Success returns the new selection Version. Reusing a stale Version returns 409; reload before another change. The same content imported twice shares inventory but its choices and audits are independent. Selection does not change recorded rights or grant publishing permission.

The source file must still be accessible, stable, safe and content-identical to the imported asset. Selection hashes it twice, holding a read-only handle through the database write. Changed bytes are rejected even if size/write timestamp is unchanged. Discovery-bound sources additionally retain their original fingerprint check. Errors include stream-source-content-changed, discovery-source-changed, media-file-changed and media-file-busy-or-unavailable. No selection/audit is committed on failure/cancellation. Large media can make the HTTP call slow and hold the selection-row lock during the final hash; configure client timeouts accordingly.

Reinspection request:

```json
{"idempotencyKey":"streams-001","maxAttempts":3}
```

Keys are 1–128 ASCII letters/digits/`.`/`_`/`:`/`-`, scoped to the import. Equal key/budget replays the same operation/job; a conflicting budget returns 409. Follow returned job.id through existing Jobs endpoints for status/progress/pause/cancel/retry/audit. Generic POST /api/jobs rejects media.stream-inspection and reserves stream-inspection: keys for the coordinator.

Reinspection uses actual source hashing and FFprobe, with matching original SHA-256/size required. It initializes old records without fake metadata. Existing inventory is immutable: conflicting probe output fails with stream-inventory-conflict; matching output preserves inventory Version and all existing choices. New stream-inspection job success, inventory/selection initialization and job audit commit together behind the execution lease. Source mismatch fails with stream-source-content-changed; unavailable/corrupt/probe timeout failures use existing import codes.

This slice inspects and records choices. It does not extract audio/subtitles, decode the full movie, run PySceneDetect, render or publish. Text and bitmap subtitle codec identities can be selected, without claiming downstream handling. Stream count is capped at 256 alongside bounded FFprobe output. Rights remain caller assertions and Unreviewed.
