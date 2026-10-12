# Publishing and scheduling — Phase 7 design, not implemented
Separate official YouTube, TikTok and Facebook adapters. Verify current permissions, account eligibility and supported automation when implementing each integration. If unattended upload is unavailable, provide a human publishing workflow.

Store protected token ciphertext and key identifiers, never plaintext access/refresh tokens. Refresh under a per-account lock. Publication requires current rights, permitted platform, expiry check, approval and validated output. Use durable publication identity and deduplication across retry/restart.

Quartz will use persistent PostgreSQL scheduling storage with UTC instants and explicit user timezone IDs, including daylight-saving ambiguity rules. Recurring schedules, cancellation and publish-attempt audits will map to domain intent. Phase 1's durable polling queue is not a Quartz scheduler and cannot schedule publication.

