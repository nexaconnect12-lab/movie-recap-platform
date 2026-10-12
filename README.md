# AI Movie Recap Automation Platform — Phase 2 media import, discovery and streams
A .NET 10 modular monolith foundation with ASP.NET Core API, independently hosted Windows-compatible processing worker, PostgreSQL/EF Core migrations, structured JSON logs, health checks and a persistent job lifecycle.

The worker executes `foundation.database-check` and `media.manual-import`. Manual import records rights/source metadata, validates stable MP4/MKV/MOV/AVI files, hashes SHA-256, deduplicates content, and extracts real FFprobe metadata. Original files remain in place. No AI account or paid service is needed. This implements the first Phase 2 slice; further capabilities are tracked in [the roadmap](docs/implementation/roadmap.md).

Before importing, configure identical `Media__Folders__library` and `Media__FfprobePath` values in both hosts and apply the additive migrations. See [manual import API/configuration](docs/api/media-imports.md), [manual-import implementation](docs/implementation/phase-2.md) and [manual-import verification](docs/implementation/phase-2-verification.md). Rights are recorded as caller assertions and remain unreviewed; import success grants no publishing permission.

Optional worker-hosted folder monitoring adds a persistent discovery inbox. It observes safe readable/stable files and recovers missed events through periodic scans; an operator must submit source/rights metadata before any import job is created. Enable with `Discovery__Enabled=true` after configuring media roots. See [discovery inbox API/configuration](docs/api/discovery-inbox.md), [implementation](docs/implementation/phase-2-discovery.md) and [current verification](docs/implementation/phase-2-discovery-verification.md). Monitoring is disabled by default; original files remain untouched.

Full FFprobe stream inventories and per-import video/audio/subtitle choices are now supported. Selection is explicit, versioned, audited and verifies the original source hash. New imports receive an inventory and unselected choice state; older records require a real reinspection job. See [streams API](docs/api/media-streams.md), [stream implementation](docs/implementation/phase-2-streams.md) and [latest verification](docs/implementation/phase-2-streams-verification.md). No audio/subtitle extraction or scene detection runs in this slice.

## Local startup (Windows PowerShell 7)
Prerequisites: .NET 10 SDK and Docker Desktop with Linux containers. Run these commands from this folder:

```powershell
./scripts/Initialize-Local.ps1
. ./scripts/Set-LocalEnvironment.ps1
docker compose -f infrastructure/compose.yml up -d --wait
dotnet tool restore
dotnet build MovieRecapPlatform.slnx -m:1
dotnet ef database update --project src/Infrastructure
dotnet run --project src/Api --no-build
```

In a second terminal, change to this same folder:

```powershell
. ./scripts/Set-LocalEnvironment.ps1
dotnet run --project src/Workers/ProcessingWorker --no-build
```

In a third terminal:

```powershell
. ./scripts/Set-LocalEnvironment.ps1
./scripts/Smoke-Test.ps1
$env:TEST_POSTGRES = $env:ConnectionStrings__Platform
$env:TEST_FFPROBE = 'D:\Tools\ffmpeg\bin\ffprobe.exe' # Use your actual executable path.
dotnet test MovieRecapPlatform.slnx -m:1 --no-build
```

API: http://127.0.0.1:5080. Health: `/health/live`, `/health/ready`. OpenAPI: `/openapi/v1.json` (requires `X-Api-Key`). [API examples](docs/api/jobs.md) include create, progress, audit and versioned commands.

Local credentials are generated once, encrypted with Windows DPAPI in ignored `.local/credentials.xml`, and shared by terminals through the environment helper. Keep them with the persisted database volume; changing POSTGRES_PASSWORD does not reset an existing PostgreSQL database. Never commit secrets. Stop API/worker with Ctrl+C; stop PostgreSQL with `docker compose -f infrastructure/compose.yml stop` after loading the environment. No service installation is necessary for development.

## Contents
- `src/Modules/Jobs/{Domain,Application}`: aggregate, validation and persistence port.
- `src/Modules/Media/{Domain,Application}`: import/assets/rights metadata, inspection and persistence ports.
- `src/Infrastructure`: PostgreSQL store, database health and generated migrations.
- `src/Api`: authenticated local management API with OpenAPI and problem responses.
- `src/Workers/ProcessingWorker`: bounded single-job executor with heartbeat and recovery.
- `tests`: unit, architectural and real PostgreSQL/API integration coverage.
- `tests/Fixtures`: owned synthetic clips for actual FFprobe tests; set `TEST_FFPROBE` as well as `TEST_POSTGRES` to verify the complete suite.
- `scripts`: environment setup, smoke test and Windows Service lifecycle.
- `docs`: plan, architecture, domain, database, future pipeline designs and deployment.

See [local development](docs/deployment/local-development.md), [Windows Service deployment](docs/deployment/windows-service.md), [architecture](docs/architecture/software-architecture.md), [verification](docs/implementation/verification.md) and [requirements](docs/requirements/product-requirements.md).

This foundation is for local development. Before remote deployment, add user authentication/authorization, TLS, rate limits and operational hardening. No publishing accounts or access tokens are persisted in Phase 1.

# movie-recap-platform
