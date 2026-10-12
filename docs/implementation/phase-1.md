# Phase 1 delivered behavior
Seven projects: Jobs Domain/Application, Infrastructure, API, Processing Worker, UnitTests and IntegrationTests. Architecture assertions use the unit runner. .NET 10 target/SDK roll-forward, explicit compatible dependencies and patched OpenAPI.NET.

Jobs support create, idempotency, query/list, audit, pause/resume/cancel/manual retry, queued/running/retry/terminal states, attempts/backoff and per-attempt progress. PostgreSQL FOR UPDATE SKIP LOCKED claims prevent simultaneous job ownership. Lease-token fencing prevents stale worker writes. Expired leases recover through retry budget/backoff. Commands and audit commit together. API mutations require current Version.

Windows-compatible worker uses validated polling/lease options, single-job capacity, heartbeat, structured logs and cancellation. It executes a real database-check maintenance operation; it does not pretend to process a movie. Logs rotate when configured. Health checks expose liveness and migrated database readiness. API requires an injected shared key and provides OpenAPI/problem responses.

Initial migration creates only implemented job/audit entities. Remaining prompt entities have ownership and phase plans rather than empty tables. Quartz is deferred to Phase 7 scheduler. Documentation describes all requested designs while labeling their unimplemented state.

Local compose, DPAPI environment helpers, smoke script and install/start/stop/uninstall scripts are supplied. Installation requires administrative deployment verification. See verification.md for observed results and roadmap.md for remaining work.

