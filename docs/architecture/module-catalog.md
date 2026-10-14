# Module catalog and future storage ownership
Jobs, Media and VideoProcessing scene detection are executable. Future modules get Domain/Application/Infrastructure boundaries when implemented. Do not create empty implementations that imply delivery.

| Module | Planned entities / responsibilities | Phase |
|---|---|---|
| Jobs | ProcessingJobs, AuditLogs (implemented); ProcessingSteps | 1, 2 |
| Media | MediaAssets, MovieImports, import-scoped MediaRights, MediaDiscoveries, stream inventories/selections/audits/inspection jobs, manual import/discovery, real FFprobe and deduplication | 2 |
| Video Processing | SceneAnalyses/MovieScenes, AudioExtractions, FrameSamplings/FrameSamples; real scenes, selected-audio WAV and scene PNG artifacts implemented; other preprocessing remains future | 2 |
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


VideoProcessing now also owns FrameSamplings/FrameSamples and the actual scene-quantile PNG artifact lifecycle. Current application table count is 16 across jobs/media/video, plus EF migration history. No later-phase module tables are created.


VideoProcessing now also owns PreparationReviews/PreparationValidations/PreparationAudits and technical preparation checks/review. Current application table count is 19 across jobs/media/video, plus EF migration history. Rights approval/revocation and later-phase module entities remain deferred.


VideoProcessing additionally owns SubtitleExtractions/SubtitleCues and canonical SRT artifacts. Latest source model has 21 application tables plus EF history; the activated local database remains at the previously deployed 19 tables until separate subtitle activation. Media owns selected-subtitle source access and choice locks; Video uses those Application contracts. No transcription/localization/rendering/publishing tables are added.
