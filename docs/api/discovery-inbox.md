# Folder monitoring and discovery inbox — Phase 2

Configure the same trusted local media roots in API and worker, and apply the MediaDiscoveryInbox migration before starting new hosts. Enable monitoring only in the worker:

```powershell
. ./scripts/Set-LocalEnvironment.ps1
$env:Media__Folders__library = 'D:\Media\Movies'
$env:Media__FfprobePath = 'D:\Tools\ffmpeg\bin\ffprobe.exe'
$env:Discovery__Enabled = 'true'
$env:Discovery__RescanSeconds = '30'
$env:Discovery__MaxEntriesPerFolder = '10000'
dotnet run --project src/Workers/ProcessingWorker --no-build
```

Defaults: disabled, periodic rescan 30 seconds (1–3600), maximum visited entries per folder 10000 (1–100000). File/directory notifications request an earlier scan, coalescing bursts. Startup/full periodic scans recover missed notifications and downtime. Files are observed across Media__StabilityMilliseconds (default 2000); Ready is metadata/readability readiness, not FFprobe acceptance. MP4/MKV/MOV/AVI are discovered recursively; unsupported files and reparse-point trees are excluded. Scan limits count all visited entries, including directories and unsupported files.

All endpoints require X-Api-Key:

| Method / path | Behavior |
|---|---|
| GET /api/media/discoveries?limit=50&status=Ready | Up to 100 newest candidates, optional status filter |
| GET /api/media/discoveries/{id} | Candidate or 404 |
| POST /api/media/discoveries/{id}/dismiss | `{ "version": "current UUID" }`; updated candidate or 404; stale version returns 409 |
| POST /api/media/discoveries/{id}/import | Submit explicit metadata; import view + Location header, 201; missing candidate 404; stale/unready/changed source 409 |

List includes retained historical revisions; `isCurrent` distinguishes the latest location revision. States: Observing, Ready, Unavailable, Submitted, Dismissed. Empty/busy files remain Observing. Complete scans mark missing candidates Unavailable. Partial/capped/unreadable scans preserve unseen candidates and log stable diagnostic codes; submission always rechecks the source rather than trusting stale availability. Changed file/location metadata creates a new candidate; old submissions remain linked to their original import. A dismissed unchanged revision stays dismissed.

Submit request:

```json
{
  "version": "<Ready candidate version UUID>",
  "source": "Original supplied by the rights holder",
  "requestedBy": "local-operator",
  "maxAttempts": 3,
  "rights": {
    "owner": "Rights holder name",
    "authorization": "Authorized",
    "licenseEvidence": "Local license reference",
    "permittedUses": "Permission description",
    "permittedPlatforms": "Named allowed platforms",
    "attribution": "Required attribution",
    "expiresAt": null
  }
}
```

Source/requester/rights validation follows [manual imports](media-imports.md). Discovery never manufactures rights or queues a job without submission. All rights remain Unreviewed for publishing, including Authorized caller assertions. The request version is both a concurrency check and a replay token: retain the original submitted version. Replaying it with identical metadata returns the same import/job even after source removal. A changed request or the candidate's new post-submission Version returns 409. The public manual-import endpoint reserves `discovery:` keys for this coordinator.

Submitted means the import was enqueued, not that processing succeeded. Follow `importId` via /api/media/imports/{id} for the authoritative job/result and use the Jobs endpoints for pause/cancel/retry. If a source changes after submission, execution fails with `discovery-source-changed` (or the existing change/unavailability code if it changes during inspection). Rescan and submit the new candidate; an old approval is never transferred to that replacement revision.

No source files are modified. Fingerprints cover location/size/write time/creation time, not content identity; SHA-256 deduplication and actual FFprobe run inside the existing import job. Keep folder roots/configuration trusted and stable. Scanner limits may require narrowing folders or raising the cap. This slice adds no dashboard, stream selection, scene detection or publishing.
