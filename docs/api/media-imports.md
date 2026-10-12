# Manual movie imports — Phase 2 slice

Both hosts require the same configuration. Choose a trusted existing local media folder and a real FFprobe executable. No folder is configured by default; database-check jobs still work.

```powershell
. ./scripts/Set-LocalEnvironment.ps1
$env:Media__Folders__library = 'D:\Media\Movies'
$env:Media__FfprobePath = 'D:\Tools\ffmpeg\bin\ffprobe.exe'
$env:Media__StabilityMilliseconds = '2000'
$env:Media__ProbeTimeoutSeconds = '30'
$env:Media__MaxProbeOutputCharacters = '1048576'
dotnet ef database update --project src/Infrastructure
```

Then start the API and worker as described in README; set these variables in both terminals. Configuration is supplied through the existing .NET configuration providers/environment injection. Absolute roots are required; changing a root while jobs are pending changes the referenced location. Keep roots and ancestors trusted and stable, without symlinks/junctions/reparse points. Files must be inside the configured root. Parent traversal, absolute paths, alternate data streams, network URLs, empty path components and unsupported extensions are rejected.

`POST /api/media/imports` requires `X-Api-Key` and JSON:

```json
{
  "idempotencyKey": "movie-owned-001",
  "folder": "library",
  "relativePath": "owned-movie.mp4",
  "source": "Original provided directly by the rights holder",
  "requestedBy": "local-operator",
  "maxAttempts": 3,
  "rights": {
    "owner": "Rights holder name",
    "authorization": "Authorized",
    "licenseEvidence": "Local license reference or evidence description",
    "permittedUses": "Commentary recap under the referenced permission",
    "permittedPlatforms": "Named platforms allowed by that permission",
    "attribution": "Required attribution text",
    "expiresAt": null
  }
}
```

Authorization is `Unknown`, `Authorized` or `Denied`. Owner is required; rights text fields must be present and may be empty for Unknown/Denied. Authorized requires nonempty evidence, permitted uses and platforms. Expiry, if supplied, uses UTC (`Z`) and is normalized to PostgreSQL microsecond precision for idempotent replays. Importing is permitted for preservation/inspection regardless of authorization status or expiry; **import success is not publishing permission**. `rightsReviewStatus` is always `Unreviewed`. Requester/owner/authorization are caller assertions under the current shared-key access model. Do not put credentials, signed secret URLs or tokens into metadata.

Success returns 201, a Location header, and `{ import, rights, rightsReviewStatus, job, asset }`. The job starts Queued and asset is null until the worker commits successful inspection. `GET /api/media/imports/{id}` returns a consistent snapshot; 404 means missing. Use `job.id` with the existing `/api/jobs/{id}` endpoints for versioned pause/resume/cancel/retry and `/audit`. No lease token is returned.

Matching idempotency keys replay the same import/job. Different request metadata with the same key returns 409. Location checks still apply to replay requests; a source that has been removed is unavailable. Keys support 1–128 ASCII letters, digits, `.`, `_`, `:`, `-`. The generic Jobs endpoint continues to accept only database-check jobs, and reserves `media-import:` keys for the import coordinator.

Source files must be nonempty and stable. Supported extensions: MP4, MKV, MOV, AVI. FFprobe must confirm a compatible container family and a real video stream with positive duration, dimensions and frame rate. Silent videos are supported. Audio codec, channels and sample rate are recorded when audio exists. This inspects metadata, not full-file decoding/corruption verification.

The worker performs read-only hashing/probing and commits the asset/result and job success together. Identical bytes share one asset; each import retains independent rights/source metadata and indicates `isDuplicate`. Errors use stable codes such as `media-file-changed`, `media-file-busy-or-unavailable`, `unsupported-media-format`, `media-container-mismatch`, `ffprobe-unavailable`, `ffprobe-rejected-media`, `ffprobe-invalid-metadata`, `ffprobe-output-too-large`, or `ffprobe-timeout`. Attempts/backoff/recovery follow Jobs rules. Fixing an import source after failure is possible before an explicit retry; audit history remains.

Worker progress is coarse (inspection stage 10, committing stage 90, success 100); heartbeat persists it periodically. Pause/cancel revoke the lease immediately; physical subprocess termination follows heartbeat detection. Sources are never changed or deleted. Downloads, folder watching, scene detection and later processing are absent.
