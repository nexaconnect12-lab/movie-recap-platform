# Project overview
The platform will orchestrate movie recap production on Windows, using modular domain boundaries and durable jobs. Phase 1 delivers infrastructure and operational scaffolding with a real foundation check. Phase 2 adds manual movie import, source/rights assertions, stable-file validation, SHA-256 deduplication, real FFprobe inventories, optional folder discovery and explicit source-verified stream selection. Sources remain in place. No scene analysis, recap generation, voice, rendering or publication is delivered yet.

Host separation permits interactive API and independent service workers without splitting the database or deploying microservices. PostgreSQL is the queue/state source. Domain behavior is deterministic and independently testable. Future workers should share contracts while owning executor adapters. Detailed topology: software-architecture.md; future ownership: module-catalog.md; priorities: ../implementation/roadmap.md.


Current Phase 2 also executes selected-video scene analysis: real PySceneDetect/PyAV, persisted PTS-derived ranges, bounded subprocesses, source verification and fenced atomic completion. See ../implementation/phase-2-scenes.md and phase-2-scenes-verification.md in that folder. Requires separately configured pinned Python dependencies. Extraction, thumbnails, semantic analysis, voice, rendering and publishing remain future.

Current Phase 2 also includes source-preserving selected-audio extraction with actual FFmpeg and managed PCM WAV artifacts. Configure a separate trusted artifact root shared by API/worker. See ../implementation/phase-2-audio.md and ../api/audio-extraction.md. No transcription, generated speech or recap rendering is included.

Current Phase 2 includes deterministic scene thumbnails/frame sampling with real FFmpeg and managed PNG batches, actual source/normalized timestamps and authenticated history/downloads. See ../implementation/phase-2-frames.md and ../api/scene-frames.md. No AI-selected highlights or later-phase output is implemented.

Current Phase 2 includes preparation view/real artifact validation and versioned audited operator decisions. Changed source/selection/prepared content or new validation makes prior decisions stale. See ../implementation/phase-2-preparation.md and ../api/preparation-review.md. Approval covers preparation only and does not grant publishing authorization or invoke later phases.
