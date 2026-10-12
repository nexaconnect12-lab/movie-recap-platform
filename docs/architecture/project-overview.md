# Project overview
The platform will orchestrate movie recap production on Windows, using modular domain boundaries and durable jobs. Phase 1 delivers infrastructure and operational scaffolding with a real foundation check. Phase 2 adds manual movie import with source/rights assertions, stable-file validation, SHA-256 deduplication and real FFprobe metadata, plus optional folder monitoring and an explicit-submission discovery inbox. Sources remain in place. No scene analysis, recap generation, voice, rendering or publication is delivered yet.

Host separation permits interactive API and independent service workers without splitting the database or deploying microservices. PostgreSQL is the queue/state source. Domain behavior is deterministic and independently testable. Future workers should share contracts while owning executor adapters. Detailed topology: software-architecture.md; future ownership: module-catalog.md; priorities: ../implementation/roadmap.md.

