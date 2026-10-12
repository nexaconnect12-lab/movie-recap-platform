# Movie Recap Platform agent instructions
Inspect the solution, documentation and applicable instructions before changing files. Do not delete or overwrite pre-existing user files without explicit confirmation. Files created during the current authorized implementation can be corrected.

Keep Domain independent of frameworks. Application owns use cases and ports; Infrastructure implements persistence and adapters. Hosts compose dependencies. Modules own their schema; use application contracts across modules, never another module's tables.

Implement only the requested phase. No simulated completion of media, AI, TTS, rendering or publishing. Never claim Burmese voice support until a real provider and voice are tested. Preserve source media and track authorization before publication. Do not build watermark concealment or platform evasion.

Keep secrets out of source, logs and database token fields. Use environment injection or protected stores. Honor cancellation and lease fencing. External side effects must be idempotent before retries are enabled.

Run dotnet build MovieRecapPlatform.slnx -m:1 and meaningful tests after changes. PostgreSQL integration tests require TEST_POSTGRES; skipped tests are not verification. Update README, docs/architecture and docs/implementation. Record architectural decisions. Report exact results, limitations and next work. Do not claim success for failing checks.

Follow .openai/implementation.md for completion reporting. Do not add agents/delegation unless the human requests it.

