# Phase 2 selected-audio extraction

Apply the additive `VideoAudioExtraction` migration and restart rebuilt hosts. Configure identical trusted Media roots and `Audio__Root` on API/worker; the worker additionally needs actual FFmpeg. The artifact root must be an absolute trusted local path, separate from every media root (neither ancestor nor descendant), accessible to both host identities. Reparse points/junctions/symlinks are rejected. No host installs tools or migrates on startup.

```powershell
$env:Audio__Root = 'D:\RecapArtifacts'
$env:Audio__FfmpegPath = 'D:\Tools\ffmpeg\bin\ffmpeg.exe'
```

An unset artifact root leaves other capabilities usable; extraction fails with `audio-artifact-root-not-configured`. Tool paths are passed without shell interpolation. FFmpeg version is read from the real executable and recorded per completion. This slice was tested with FFmpeg 9.0.2, not a guarantee that every codec/build will decode.

| Setting | Default | Bounds / purpose |
|---|---|---|
| Audio__Root | unset | Absolute dedicated artifact root; configure on both hosts |
| Audio__FfmpegPath | ffmpeg | Worker executable name/path |
| Audio__TimeoutSeconds | 3600 | 1–86400; entire version/decode execution budget |
| Audio__MaxPcmBytes | 1073741824 | 2–4294967258; decoded PCM cap, exceeding it fails rather than truncates |
| Audio__MaxDiagnosticCharacters | 65536 | 1024–1048576; bounded stdout for version/stderr for tools |

Select an eligible audio stream explicitly with the existing [stream API](media-streams.md). No selected audio (including silent/no-audio choice) conflicts; no language/default-track guess is made. POST `/api/media/imports/{importId}/audio-extractions`, authenticated with `X-Api-Key`:

```json
{
  "idempotencyKey": "audio-001",
  "selectionVersion": "CURRENT-SELECTION-UUID",
  "requestedBy": "local-operator",
  "maxAttempts": 3
}
```

Keys accept 1–128 ASCII letters/digits/`._:-`; requester is required, at most 256 characters; attempts 1–5. The new request requires the current selection version. Matching import-scoped request parameters replay the original operation even after the selection changes; conflicting parameters return 409. Changed choices require a new key/current version for fresh extraction. Invalid request fields return 400; missing import 404. Response 201/Location exposes `extraction`, `job` and `isCurrentSelection`.

GET `/api/video/audio-extractions/{id}` returns operation/job metadata. GET `/api/media/imports/{importId}/audio-extractions?limit=50` returns history (limit clamped 1–100). Poll the shared job; existing [pause/resume/cancel/retry/audit endpoints](jobs.md) apply. Internal `video.audio-extraction` jobs and `audio-extraction:` keys cannot be created through generic Jobs POST.

GET `/api/video/audio-extractions/{id}/artifact` serves an authenticated WAV download after checking its full SHA-256, exact RIFF/PCM header and byte/sample length under a retained read handle. Pending/failed operations return 409; missing operations 404; missing/corrupt artifact returns a stable `audio-*` error. Local storage paths are not API inputs/response fields. Successful old artifacts remain available after selection changes, with `isCurrentSelection=false`; this flag describes the choice binding, not a new source hash verification at query time.

POST `/api/video/audio-extractions/{id}/cleanup` reconciles abandoned/unreferenced attempt files for this operation and returns `removedFiles`. It preserves the committed artifact, active attempts and unknown files. Executors also reconcile before starting and after exiting. Failed cleanup/abrupt termination may leave hidden files until the next retry or explicit cleanup; there is no background global retention service. Ownership markers and small lock files/directories are retained. No deletion API for completed history is provided.

The fixed versioned profile is `wav-pcm-s16le-16000-mono-v1`: FFmpeg maps the exact chosen global stream, decodes/resamples/downmixes to 16 kHz mono signed 16-bit PCM, with video/subtitles/data and source metadata excluded. The host writes a canonical WAV header from actual decoded sample count. This is a derived local analysis artifact, not lossless preservation of original channels/codec. The normalized sample timeline is `samples/16000`; original timestamps, gaps and video alignment are not an exported timing contract in this slice. No transcript/voice/recap is generated.

Before/after decode, the original file hash/size must match the imported asset under a retained read-only source handle. Selection changes before/during execution reject publication. Each lease token gets a unique temporary file; full validation/flush precedes non-overwriting same-directory promotion. The job/selection locks and lease recheck fence the database reference. A crash or rollback after rename leaves an unreferenced file that is never downloadable and can be reconciled. No source file is replaced, moved or removed. Timeout/cancellation kills the tool process tree. Progress is coarse output-cap based. No diagnostics, media paths or secrets are persisted as token/error fields.

Limits: trusted local storage/identities, same root visible to all executing hosts, per-attempt size/time limits rather than a global disk quota, full hash passes and download hashing, no OS decoder memory sandbox, and no cross-resource atomic filesystem/PostgreSQL transaction. Backup/restore must preserve both database references and artifact tree. Rights remain caller assertions and Unreviewed; extraction grants no publication approval. ASR/AI/TTS, subtitles, thumbnails, rendering and publishing remain separate work.
