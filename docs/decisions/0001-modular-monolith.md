# ADR 0001 — Modular monolith with independent worker
Status: accepted, 2026-10-12.
Use one solution and shared PostgreSQL with domain/application contracts, module-owned schemas and independently hosted API/worker. Domain has no persistence dependencies. This meets Phase 1 needs without a broker/microservices. Extraction later requires explicit contracts and idempotent side effects; there is no implied distributed exactly-once guarantee.

