# Module catalog and future storage ownership
Jobs and the Phase 2 manual-import Media slice are executable. Future modules get Domain/Application/Infrastructure boundaries when implemented. Do not create empty implementations that imply delivery.

| Module | Planned entities / responsibilities | Phase |
|---|---|---|
| Jobs | ProcessingJobs, AuditLogs (implemented); ProcessingSteps | 1, 2 |
| Media | MediaAssets, MovieImports, import-scoped MediaRights, MediaDiscoveries, manual import/discovery inbox, real FFprobe metadata and deduplication | 2 |
| Video Processing | MovieScenes, stream/probe analysis and preprocessing artifacts | 2 |
| AI Analysis | Transcripts, AIAnalysisResults, source evidence/confidence | 3 |
| Recap Generation | RecapProjects, RecapScripts, RecapSegments | 3 |
| Localization | LocalizationVersions, language policies | 4 |
| Voice and Subtitles | VoiceAssets, SubtitleAssets, timing/alignment | 4 |
| Video Rendering | RenderJobs, RenderOutputs, output validation | 5 |
| Rights | Import-scoped source/license/platform assertions recorded by Media; separate approval/revocation/publication service remains future | 2 foundations; 7 enforcement |
| Publishing | PublishingAccounts, PublishingAttempts, PublishedVideos | 7 |
| Scheduling | PublishingSchedules, persistent Quartz tables | 7 |
| Administration | SystemSettings, dashboard read models | 6 |

Cross-module references use identities and explicit contracts. Avoid cross-module table reads and EF navigation across aggregate boundaries. Each module owns migrations/schema; orchestration coordinates use cases and emits audit evidence. Account token ciphertext must use a protected key store; no token fields exist today.

