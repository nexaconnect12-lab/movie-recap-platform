# Phase 2 scene analysis API and configuration

Requires completed import, real stream inventory and an explicit eligible video selection. Apply `VideoSceneAnalysis` and restart rebuilt hosts. The worker needs a compatible Python environment; nothing is installed automatically. All routes require `X-Api-Key` under the existing shared-key access model.

Create a local Python 3.12 environment and install the pinned runner dependencies:

```powershell
python -m venv .local/scene-venv
./.local/scene-venv/Scripts/python.exe -m pip install -r src/Infrastructure/Scenes/requirements.txt
$env:Scenes__PythonPath = "$PWD/.local/scene-venv/Scripts/python.exe"
```

Use an actual Python executable path accessible to the worker account, including when running as a Windows Service. PySceneDetect 0.7.1, PyAV 19.0.0, OpenCV 5.0.0.93 and their dependencies are pinned. The runner checks PySceneDetect/PyAV versions and records Python/OpenCV versions. Both hosts validate scene options; only the worker launches Python. `Scenes__RunnerPath` defaults to the bundled `Scenes/scene_runner.py` in host output. Rebuild/publish copies the runner and requirements; copying just a DLL is insufficient.

| Setting | Default | Meaning |
|---|---|---|
| Scenes__TimeoutSeconds | 3600 | Decode subprocess timeout; allowed 1–86400 |
| Scenes__MaxFrames | 2000000 | Decoded-frame/timestamp storage bound; allowed 1–10000000 |
| Scenes__MaxScenes | 10000 | Range count bound; allowed 1–10000 |
| Scenes__MaxVideoSeconds | 86400 | Maximum normalized decoded duration; allowed 1–86400 |
| Scenes__MaxFramePixels | 33177600 | Decoded frame pixel cap; allowed 1–100000000 |
| Scenes__MaxOutputCharacters | 4194304 | Each stdout/stderr total bound; allowed 1024–16777216 |

POST `/api/media/imports/{importId}/scene-analyses`:

```json
{
  "idempotencyKey": "movie-scenes-001",
  "selectionVersion": "CURRENT-SELECTION-UUID",
  "requestedBy": "local-operator",
  "threshold": 27,
  "minimumSceneFrames": 15,
  "maxAttempts": 3
}
```

Settings default to those shown. Threshold is finite 1–255; minimum length is 1–3000 decoded frames; attempts 1–5. Request keys accept 1–128 ASCII letters/digits/`._:-`; requester is required, at most 256 characters. A new request requires the current selection version. Matching import-scoped key/parameters replay the original analysis, even after the choice changes; changed parameters conflict. A new choice requires a new key/version for fresh analysis. Creation atomically binds import/asset/inventory/selection/hash/size/video index/settings/operator to a durable job and creation audit. Response 201 includes `analysis`, `job`, `isCurrentSelection`, `scenes` and a Location header. Invalid settings return 400; version/selection/idempotency conflicts 409; missing import 404.

GET `/api/video/scene-analyses/{analysisId}` returns the operation, shared job status view and persisted ranges. GET `/api/media/imports/{importId}/scene-analyses?limit=50` lists history (limit clamped to 1–100). `scenes` stays empty until fenced atomic success. Read `job.id` for existing [job status/audit/pause/resume/cancel/retry commands](jobs.md). `video.scene-analysis` cannot be created through generic Jobs POST; coordinator keys are reserved.

Ranges use zero-based decoded `startFrame`, exclusive `endFrame`, and normalized `startSeconds`/exclusive `endSeconds`. They cover the entire selected decoded stream in order with no gaps/overlap. Completion records actual timestamp-derived duration, original first-frame `sourceOffsetSeconds`, variable-frame-rate observation, algorithm `content/suppress`, settings and runtime versions. Seconds use presentation timestamps, including VFR; nominal FPS is only detector frame numbering, never a fabricated timeline. Invalid/nonmonotonic/missing timing or unavailable final duration fails explicitly.

Source bytes are hashed against the imported asset before and after decoding, under a retained read-only handle. PyAV directly decodes the chosen global video index. No remux, extracted media, thumbnails or source mutation occurs. A selection change before/during execution rejects completion; previous successful ranges remain immutable history with `isCurrentSelection=false`. Pause/cancel/lease expiry cancels the subprocess and fences database writes. Retry recomputes rather than appending partial ranges; it remains bound to its original choice. Resource/runtime/decode errors use stable `scene-*` codes and normal job attempt budgets; raw decoder diagnostics are bounded and discarded. Progress is coarse and frame-cap based, not a playback-time percentage.

This is scene boundary detection, not semantic movie understanding. Fades/camera motion can affect ContentDetector results. Rights remain caller assertions and Unreviewed; success grants no publishing approval. Audio/subtitle extraction, thumbnails, preprocessing, AI, voice, rendering and publishing require separate work.
