# Jobs API
All non-health endpoints require X-Api-Key. OpenAPI JSON: GET /openapi/v1.json. Status values serialize as strings. Health/live indicates process liveness; health/ready also verifies migrated PostgreSQL persistence.

| Method / path | Result |
|---|---|
| POST /api/jobs/ | Create or return existing matching idempotent job, 201 |
| GET /api/jobs/?limit=50 | Latest 1–100 job views |
| GET /api/jobs/{id} | Job view or 404 |
| GET /api/jobs/{id}/audit | Complete audit or 404 |
| POST /api/jobs/{id}/pause | Pause queued/retry/running job |
| POST /api/jobs/{id}/resume | Resume paused job |
| POST /api/jobs/{id}/cancel | Cancel active/paused job |
| POST /api/jobs/{id}/retry | Reset failed job budget and requeue |

Creation:
```json
{"kind":"foundation.database-check","idempotencyKey":"example-001","maxAttempts":3}
```
Idempotency key supports ASCII letters/digits and . _ : -; max 128 characters. The `media-import:` namespace is reserved. Other job kinds are rejected by this generic creation endpoint; manual movie jobs must be created through [the Media import endpoint](media-imports.md) to atomically associate source/rights metadata. MaxAttempts 1–5. All query/audit/versioned commands above also apply to `media.manual-import` jobs.

Mutation body:
```json
{"version":"<current version UUID from GET response>"}
```
Use a fresh version after each mutation/heartbeat. Stale versions and invalid transitions return 409, validation returns 400, internal errors return generic 500. Missing/wrong key returns 401. Lease token is never serialized to clients.

PowerShell example:
```powershell
. ./scripts/Set-LocalEnvironment.ps1
$headers = @{'X-Api-Key'=$env:Security__ApiKey}
$job = Invoke-RestMethod http://127.0.0.1:5080/api/jobs/ -Method Post -Headers $headers -ContentType application/json -Body '{"kind":"foundation.database-check","idempotencyKey":"example-001","maxAttempts":3}'
Invoke-RestMethod "http://127.0.0.1:5080/api/jobs/$($job.id)" -Headers $headers
```
Use scripts/Smoke-Test.ps1 for a complete create → worker execution → success/audit check. Future API work: user roles, durable cursor pagination, rate limiting and production gateway/authentication.

