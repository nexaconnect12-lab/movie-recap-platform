# Phase 1 architecture and implementation plan
Inspected the supplied working directory on 2026-10-12: only empty work and outputs folders existed, with no solution or applicable AGENTS.md. Specification: attached Codex Master Prompt — AI Movie Recap Automation Platform (Pasted text.txt). Deliverables were initially created in outputs/MovieRecapPlatform. The canonical project location is now D:\DevOps\movie-recap-platform, as requested by the user; the existing matching copy was retained without replacing source files.

1. Create net10.0 projects and clean dependency boundaries.
2. Implement a Jobs domain aggregate with explicit states, attempts, progress, leases and version checks.
3. Implement PostgreSQL persistence, atomic claim and state/audit transactions; generate EF migrations.
4. Add validated API contracts, protected endpoints, health and OpenAPI.
5. Add a Windows-compatible worker, heartbeat, bounded execution and a real foundation database check.
6. Add unit/boundary tests and actual PostgreSQL/API concurrency and recovery tests.
7. Add local compose/setup and service scripts; document architecture, schemas, future modules and exact usage.
8. Build, run tests, apply migrations and smoke-test both processes. Record evidence and limitations.

Assumptions: .NET 10 SDK available; PostgreSQL 17 local Docker instance; no existing schema to preserve. Phase 1 has one job kind and one executable worker, resource concurrency = 1 per process. Future module schemas and Quartz arrive with their feature phases. No placeholder business entities, provider integrations or dashboard are counted as implemented. UTC timestamps assume synchronized host clocks.

Phase 2 manual-import changes were proposed after inspecting the existing solution/instructions and confirmed by the user on 2026-10-12. Scope and decisions are in phase-2.md and ADR 0005. Existing hosts were found running; verification uses separate artifact outputs and disposable databases/processes so those hosts and the persisted application database stay intact. No further phase is included.

