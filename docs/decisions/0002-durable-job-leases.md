# ADR 0002 — PostgreSQL queue with expiring fenced leases
Status: accepted, 2026-10-12.
Use FOR UPDATE SKIP LOCKED to claim one available job, then commit a generated token and expiry. Heartbeats renew ownership; API pause/cancel invalidates it. Stale tokens/versions cannot mutate a job. On failure/expiry apply bounded exponential backoff. Save audits atomically.

Delivery is at least once. PostgreSQL state writes are fenced; future FFmpeg/provider/publishing operations require idempotent external effects. UTC clocks must be synchronized. No general cron/timezone recurrence exists; persistent Quartz is reserved for Phase 7, consistent with the prompt's scheduler implementation phase.

