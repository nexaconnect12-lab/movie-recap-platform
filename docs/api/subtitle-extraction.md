# Selected embedded subtitles — Phase 2

This slice supports explicitly selected embedded **SubRip** (`subrip`) tracks. It extracts actual timed text with FFmpeg into canonical UTF-8 SRT and persists cue metadata. Other codecs, bitmap subtitles, OCR, burned-in text, translation, speech recognition and generated narration subtitles are outside this profile. Original media stays read-only; no subtitle removal, rendering or publication occurs.

Apply additive `VideoSubtitleExtraction`, rebuild/restart both hosts and configure identical trusted artifact roots. The activated local release has not been upgraded by this implementation; see the verification report before separately scoped activation.

```powershell
$env:Subtitles__Root = 'D:\RecapArtifacts'
$env:Subtitles__FfmpegPath = 'D:\Tools\ffmpeg\bin\ffmpeg.exe'
```

The root may be shared with Audio/Frames through its separate `subtitle-v1` namespace. It must be absolute, trusted, without reparse points and disjoint from media roots. Ownership markers protect pre-existing unowned directories. API and worker need the same roots/source access; the worker additionally needs real FFmpeg.

| Setting | Default | Allowed |
|---|---|---|
| Subtitles__Root | unset | Trusted absolute artifact root |
| Subtitles__FfmpegPath | ffmpeg | Actual executable path/name |
| Subtitles__TimeoutSeconds | 3600 | 1–86400 |
| Subtitles__MaxBytes | 8388608 | 16–16777216, extraction/canonical artifact limit |
| Subtitles__MaxDiagnosticCharacters | 65536 | 1024–1048576 |

All routes require `X-Api-Key`. POST `/api/media/imports/{importId}/subtitle-extractions`:

```json
{
  "idempotencyKey": "selected-subtitles-001",
  "selectionVersion": "CURRENT-STREAM-SELECTION-UUID",
  "requestedBy": "local-operator",
  "maxAttempts": 3
}
```

Keys use 1–128 ASCII letters/digits/`._:-`, actor is required/max 256, attempts 1–5. Select a subtitle first; explicit no-subtitle choice is valid preparation but cannot request extraction. Unsupported selected codec returns `subtitle-codec-unsupported` without creating a job. Missing import returns 404, stale choices/conflicting request metadata 409 and invalid request/configuration/artifact checks 400. Matching import-scoped keys replay the original operation, including after selection changes; changed parameters conflict.

Response 201/Location contains `extraction`, `job`, `isCurrentSelection`, `cues`. Cues remain empty until successful publication. GET `/api/video/subtitle-extractions/{id}` returns source/selection/profile/job/provenance and ordered cues. GET `/api/media/imports/{importId}/subtitle-extractions?limit=5` lists history (clamped 1–100). Responses include all cues per extraction; use small history limits for long tracks. Each cue has zero-based ordinal, source start/end milliseconds and text. Stored text is data; clients must escape it rather than treating subtitle markup as trusted HTML.

GET `/api/video/subtitle-extractions/{id}/artifact` serves `application/x-subrip; charset=utf-8` after full UTF-8/cue/canonical-byte/SHA-256 verification under a held read handle. Unfinished operation conflicts; missing operation returns 404. Successful historical files remain downloadable after choices change. The current-selection flag describes persisted binding, not a fresh source hash observation. POST `/{id}/cleanup` removes only unreferenced abandoned `.tmp`/`.srt` files, preserving committed references, active attempt locks, ownership/lock markers and unknown files. Generic Jobs POST cannot impersonate internal `video.subtitle-extraction` or `subtitle-extraction:` keys. Pause/resume/cancel/retry/audit use existing Jobs routes.

The fixed profile is `srt-utf8-source-timestamps-v1`: strict UTF-8, LF, no BOM, sequential SRT numbering and actual FFmpeg-exported source times without scene-offset rebasing. Cue text is retained as produced by FFmpeg; packet-level formatting equivalence is not claimed. Positive five-second offsets and multiline UTF-8 are verified with real input. Strict exported timing requires nonnegative start, end greater than start and less than 100 hours. Up to 100,000 cues and 4,096 UTF-16 characters per cue are accepted; starts must be nondecreasing. Overlaps/equal starts are valid; malformed/empty tracks, invalid UTF-8 and forbidden control characters fail. Empty cue lists are not successful extraction.

Bounded subprocess output is validated/canonicalized before file promotion. No provider diagnostics are persisted. Source handles/hash checks, unique lease-attempt files, non-overwriting promotion and lease/selection checks protect retries. Cue rows, artifact reference and job/audit success commit atomically. Filesystem and database are not one transaction; interrupted publication may leave an invisible orphan for reconciliation. No global retention/quota or OS decoder memory sandbox is added.

Preparation now requires a current completed subtitle artifact when a subtitle is selected, includes it in the fingerprint/history, and rechecks exact cues/bytes/hash during validation and fresh approval. Changed subtitle artifacts make decisions stale. New bindings use schema version 2; migration defaults historical bindings to version 1 so original fingerprints remain inspectable. No-subtitle fingerprints retain their original value. Earlier approvals with selected subtitles become stale/incomplete until extraction and revalidation; no approvals are fabricated. Rights remain unchanged/Unreviewed and technical approval grants no publishing permission.
