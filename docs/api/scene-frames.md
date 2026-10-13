# Phase 2 scene thumbnails and frame sampling

Apply additive `VideoFrameSampling`, rebuild/restart both hosts and configure a trusted absolute artifact root accessible to both identities. A worker needs actual FFmpeg; completed real scenes also require the existing Media/PySceneDetect setup. No tools/dependencies/migrations are installed or run automatically at startup.

```powershell
$env:Frames__Root = 'D:\RecapArtifacts'
$env:Frames__FfmpegPath = 'D:\Tools\ffmpeg\bin\ffmpeg.exe'
```

Frames and audio may share the dedicated artifact root, using separate `frames-v1`/`audio-v1` namespaces. The root cannot overlap a Media root in either direction; links/reparse points are rejected. Ownership markers protect pre-existing unowned operation directories. An unset root leaves other use cases available but frame jobs fail explicitly.

| Setting | Default | Purpose / allowed bounds |
|---|---|---|
| Frames__Root | unset | Trusted local artifact root, configured identically on both hosts |
| Frames__FfmpegPath | ffmpeg | Worker executable name/path |
| Frames__TimeoutSeconds | 3600 | Entire version/decode/staging budget, 1–86400 |
| Frames__MaxImageBytes | 1048576 | Per-image encoded PNG cap, 1024–4194304 |
| Frames__MaxBatchBytes | 104857600 | Total encoded PNG cap, 1024–1258291200 |
| Frames__MaxDiagnosticCharacters | 1048576 | Bounded tool diagnostics/version output, 1024–4194304 |

All endpoints require `X-Api-Key`. POST `/api/video/scene-analyses/{analysisId}/frame-samplings`:

```json
{
  "idempotencyKey": "scene-frames-001",
  "requestedBy": "local-operator",
  "samplesPerScene": 1,
  "maxAttempts": 3
}
```

The analysis must be successfully completed and bound to the current explicit video selection. Sampling inherits its immutable import/asset/hash/size/inventory/selection/video/offset binding through the local analysis reference. New requests against unfinished/noncurrent analyses conflict. Keys accept 1–128 ASCII letters/digits/`._:-`; requester is required and at most 256 characters. Samples per scene is 1–3; attempts 1–5. Short scenes receive at most one sample per decoded frame, without duplicates. A whole request is limited to 300 samples; exceeding it fails explicitly without dropping scenes.

Response 201/Location includes `sampling`, `analysis`, `job`, `isCurrentSelection`, and `samples`. Matching analysis-scoped keys/parameters replay the original request even after the video choice changes; changed parameters conflict. Invalid fields/limits return 400, missing analysis 404 and unavailable/current-selection conflicts 409. No arbitrary timestamps/filter expressions/paths/image dimensions are accepted from clients.

GET `/api/video/frame-samplings/{id}` returns status, binding/provenance and planned/completed sample metadata. GET `/api/video/scene-analyses/{analysisId}/frame-samplings?limit=50` lists history (limit clamped 1–100). Samples show ordinal, source scene ordinal, decoded frame index, normalized scene bounds and nullable actual source/normalized seconds/hash/size. Planned rows exist while queued but no file is downloadable until the entire batch/job succeeds. Existing [Jobs lifecycle/status/audit commands](jobs.md) provide pause/resume/cancel/retry. `video.frame-sampling` and `frame-sampling:` keys are reserved for this coordinator.

GET `/api/video/frame-samplings/{id}/samples/{ordinal}` serves an authenticated `image/png` download after full CRC/pixel/header/size/hash validation under a retained read handle. Unfinished batches conflict; missing batches/ordinals return 404. Successful history remains downloadable after selection changes and is marked noncurrent. This flag describes binding, not a fresh source hash check when reading metadata. Artifact root paths are never client inputs or response fields.

POST `/api/video/frame-samplings/{id}/cleanup` returns `removedFiles` for abandoned/unreferenced PNGs. It holds the operation advisory lock and preserves the durable batch reference, active attempt locks and unknown entries. Executors reconcile before/after execution. Crashed/failed/cancelled operations without another execution may need this explicit call. Ownership/lock markers and committed history remain; no global janitor or completed-history deletion API is added.

The fixed profile is `png-rgb24-320x180-frame-quantiles-v1`. Sampling chooses decoded frame quantiles within each end-exclusive scene frame range (one sample selects its middle frame). FFmpeg maps the exact global video index, decodes from the beginning through requested samples without approximate seeking/FPS conversion, preserves input timestamps, and reports integer PTS/timebase through a named showinfo filter. Actual `sourceSeconds` is PTS×timebase; `normalizedSeconds = sourceSeconds − analysis.sourceOffsetSeconds`. Every sample must fall inside its stored scene time range; mismatched decoder timelines fail rather than invent timing. VFR timing never comes from frame index divided by nominal FPS.

Images fit the encoded pixel frame into 320×180 without cropping, pad unused space black, use square pixels and RGB24/noninterlaced PNG. Automatic rotation is disabled; this profile does not implement display-orientation/SAR-aware presentation corrections. Padding/resizing is only a thumbnail fit; no content/ownership watermark concealment is implemented. Audio/subtitles/data and source metadata are excluded from outputs.

Source verification retains a read-only handle and hashes before/after decode. Every lease gets its own temporary batch directory and exclusive lock. PNG validation checks fixed IHDR, chunk CRCs, bounded full zlib/pixel decoding and SHA-256; files are flushed before a non-overwriting directory rename. Publication locks/checks the job and current selection, revalidates output, promotes the batch, rechecks expiry, then commits all sample metadata/reference/job/audit together. Filesystem/PostgreSQL are not one transaction: a crash/rollback can leave an invisible orphan, reconciled against the durable reference. Timeouts/cancellation kill the subprocess tree; provider diagnostics are bounded/discarded except validated timing/version fields. Progress is sampled-image-count based and stays below completion.

Limits: at most 300 images, full source hashes and sequential decode can be expensive, per-attempt output/time limits without aggregate quotas or an OS decoder memory sandbox, trusted shared-root deployment, no global retention service and no semantic representativeness guarantee. Frames are deterministic technical samples, not AI-selected highlights. Rights remain import-specific caller assertions and Unreviewed. AI/ASR/TTS, subtitle extraction, recap rendering, UI and publishing remain future work.
