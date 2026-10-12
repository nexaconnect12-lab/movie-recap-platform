# Implementation workflow
1. Inventory files and requested phase; preserve user files.
2. Read README, ADRs and architecture. State assumptions.
3. Implement domain behavior first and persist state/audit atomically.
4. Exercise failure, cancellation, concurrency and recovery paths against PostgreSQL.
5. Build all hosts, run tests, review migration SQL, and smoke-test API + worker.
6. Synchronize architecture, database and implementation docs.
7. Report changed areas, exact check counts, commands, missing capabilities and next task.
Production side effects, account tokens, TTS claims and service installation require explicit scoped work. Normal local builds/tests are authorized by implementation requests.

