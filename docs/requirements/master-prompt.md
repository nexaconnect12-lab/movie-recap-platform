# Codex Master Prompt — AI Movie Recap Automation Platform

## 1. Role and Objective

Act as a **Principal Software Architect, Senior .NET Engineer, AI Engineer, Video Processing Engineer, and DevOps Engineer**.

Build a production-oriented **AI Movie Recap Automation Platform** running primarily as Windows Services.

The application must automatically process imported full-length movies, generate AI movie recaps, produce localized AI narration and synchronized subtitles, render videos, and schedule publishing to YouTube, TikTok, and Facebook.

**Important:** Implement this project incrementally. Do not generate placeholder implementations and claim features are complete. Every phase must compile, be testable, and have updated documentation.

## 2. Technology Stack

Use the following preferred technologies:

- Backend: C# and .NET 10 LTS
- Background processing: .NET Worker Services with Windows Service hosting
- Architecture: Domain-Driven Design, Clean Architecture, modular monolith initially
- API: ASP.NET Core Web API
- Dashboard: React, TypeScript, Vite
- Database: PostgreSQL with Entity Framework Core
- Job scheduling: Quartz.NET
- Logging: Serilog
- Validation: FluentValidation
- Video processing: FFmpeg and FFprobe
- Scene detection: Python and PySceneDetect, with a well-defined integration boundary
- AI integration: Provider-agnostic abstraction supporting OpenAI and configurable alternatives
- Speech recognition: Whisper-compatible ASR
- Text-to-speech: Configurable provider with verified Burmese language support
- Subtitle formats: SRT, WebVTT, ASS
- Testing: xUnit, integration tests
- API documentation: OpenAPI
- Local infrastructure: Docker Compose for PostgreSQL and supporting dependencies

Prefer stable, compatible package versions. Do not assume that a particular model supports Burmese speech synthesis without verification.

## 3. Main Functional Requirements

### FR-001 — Movie Import

Implement automatic movie import using:

- Folder monitoring
- Manual import through API/dashboard
- Supported input formats: MP4, MKV, MOV, AVI
- File validation
- Duplicate detection using file hashes
- Metadata extraction using FFprobe
- Import progress and status tracking
- Configurable input, processing, output, archive, and failed-job folders

Do not begin processing files that are still being copied.

### FR-002 — Video Preprocessing

Implement a preprocessing pipeline that can:

- Detect video, audio, and subtitle streams
- Remove or exclude selectable subtitle streams from the output
- Detect burned-in subtitles and other visual overlays
- Detect black bars
- Normalize resolution, frame rate, and audio where necessary
- Extract scenes, thumbnails, and audio
- Preserve the original input file

Support cropping, masking, and inpainting of overlays where technically appropriate and authorized.

Do not remove ownership watermarks or attribution to conceal copyrighted source material. Track the source and usage rights for every imported asset.

### FR-003 — AI Movie Understanding

Implement an AI analysis pipeline that:

- Detects scene boundaries
- Extracts representative frames
- Transcribes dialogue
- Analyzes visual and dialogue context
- Identifies characters and major events
- Produces a chronological story outline
- Identifies key scenes relevant to the recap
- Creates a structured timeline with source timestamps
- Stores confidence levels and analysis results

Use chunked processing for long movies to manage memory and AI token costs.

The system must avoid inventing plot events that are not supported by the source.

### FR-004 — AI Recap Generation

Generate an original narrative recap based on the analyzed movie.

Support configurable target durations:

- 5 minutes
- 10 minutes
- 15 minutes
- 20 minutes
- AI-selected duration

Generate:

- Recap title
- Story summary
- Scene selection plan
- Narration script
- Scene-to-narration mapping
- Video editing timeline
- Description and tags
- Thumbnail suggestions

Support three recap modes:

1. Authorized original movie clips with AI narration
2. Newly AI-generated visuals with AI narration
3. Hybrid of authorized movie clips and generated visuals

Ensure the narrative is coherent, chronological where appropriate, and suitable for spoken delivery.

### FR-005 — Multilingual Localization

Implement localization support with Burmese as the primary language.

The initial system must support:

- Burmese narration scripts
- Burmese subtitle text
- English as a secondary language
- Configurable additional languages
- Unicode normalization
- Language-specific punctuation and sentence splitting
- Terminology consistency
- Proper names and character-name handling

Design language support using extensible interfaces rather than hardcoding Burmese throughout the application.

### FR-006 — AI Voice Generation

Implement AI text-to-speech generation.

Requirements:

- Burmese TTS provider integration
- Configurable voices
- Adjustable speaking rate
- Audio chunk generation
- Retry handling
- Audio concatenation
- Silence management
- Audio normalization
- Narration duration calculation
- Voice preview support

Provide a mock TTS implementation for development and tests.

Do not claim Burmese voice generation is supported until a real provider and voice have been tested successfully.

### FR-007 — Automatic Subtitle Generation

Generate subtitles from the final narration.

Requirements:

- Sentence- or phrase-level timing
- Synchronization with generated speech
- SRT and ASS export
- Optional burned-in subtitles
- Configurable font, position, size, outline, and styling
- Proper Burmese Unicode shaping
- Safe margins for vertical video
- Subtitle preview
- Timing validation

Use forced alignment or provider speech timestamps when available.

### FR-008 — Automatic Video Rendering

Implement an FFmpeg-based rendering pipeline.

Support:

- Scene trimming
- Clip concatenation
- Voice-over synchronization
- Background audio mixing
- Subtitle overlays
- Transitions
- Resolution conversion
- Thumbnail generation
- Final output validation

Output profiles:

- YouTube landscape: 1920×1080, 16:9
- YouTube Shorts: 1080×1920, 9:16
- TikTok: 1080×1920, 9:16
- Facebook Reels: 1080×1920, 9:16

Make encoding settings configurable.

Implement cancellation, retries, temporary-file cleanup, progress reporting, and hardware encoding when available.

### FR-009 — Copyright and Rights Management

Implement a rights-aware processing system.

Each imported movie must have:

- Source information
- Rights owner, if known
- License or authorization information
- Permitted usage types
- Permitted distribution platforms
- Attribution requirements
- Expiration date, if applicable
- Approval status

Prevent automatic publication unless the content has the required authorization and passes the configured approval workflow.

Do not implement copyright-detection evasion, Content ID circumvention, watermark removal intended to disguise ownership, or claims that a particular editing format is guaranteed copyright-free.

The platform may support original commentary, analysis, and transformative editing, but must not assume these automatically establish legal permission.

### FR-010 — Publishing Integrations

Create separate publisher adapters for:

- YouTube Data API
- TikTok Content Posting API
- Facebook Graph API

Use official, documented interfaces and verify current permissions, upload requirements, and account eligibility.

Support:

- OAuth account connections
- Secure token storage
- Token refresh where supported
- Video upload
- Title and description
- Tags and hashtags where supported
- Thumbnail upload where supported
- Privacy settings
- Scheduled publishing
- Retry policies
- Upload status monitoring
- Publication history
- Error reporting

If a platform or account does not support fully automated publication, implement a manual publishing or approval workflow instead of using unauthorized browser automation.

### FR-011 — Scheduler

Use Quartz.NET to schedule processing and publishing.

Requirements:

- Scheduled publishing date and time
- Timezone-aware scheduling
- Recurring schedules where appropriate
- Job queue
- Retry and backoff
- Duplicate publishing prevention
- Job cancellation
- Job pause and resume
- Failed-job recovery
- Audit history

Use persistent scheduling storage.

### FR-012 — Administration Dashboard

Build a React and TypeScript dashboard with:

- Overview and statistics
- Movie import management
- Processing job list
- Job details and progress
- AI recap script editor
- Voice and subtitle settings
- Video preview
- Approval workflow
- Connected publishing accounts
- Publishing calendar
- Publication history
- Error logs
- System settings
- AI usage and cost estimates

Use responsive layouts and a clean professional interface.

## 4. Architecture Requirements

Use Domain-Driven Design and Clean Architecture.

Create the following logical modules:

- Media Management
- Video Processing
- AI Analysis
- Recap Generation
- Localization
- Voice and Subtitle Generation
- Video Rendering
- Rights Management
- Publishing
- Scheduling
- Administration

Separate domain logic, application use cases, infrastructure integrations, and API presentation.

Use dependency injection, strongly typed configuration, cancellation tokens, structured logging, and resilient external-service clients.

Initially deploy as a modular monolith with independently hosted background workers. Design clear boundaries to permit future microservice extraction.

Do not introduce Kubernetes or distributed messaging unless justified by actual requirements.

## 5. Database Design

Design PostgreSQL entities and migrations for at least:

- MediaAssets
- MediaRights
- MovieImports
- ProcessingJobs
- ProcessingSteps
- MovieScenes
- Transcripts
- AIAnalysisResults
- RecapProjects
- RecapScripts
- RecapSegments
- LocalizationVersions
- VoiceAssets
- SubtitleAssets
- RenderJobs
- RenderOutputs
- PublishingAccounts
- PublishingSchedules
- PublishingAttempts
- PublishedVideos
- AuditLogs
- SystemSettings

Use appropriate relationships, indexes, concurrency handling, and lifecycle states.

Never store access tokens or secrets in plaintext.

## 6. Windows Service Requirements

Implement worker services that can:

- Run without an interactive user session
- Start automatically with Windows
- Recover from unexpected failures
- Resume incomplete jobs safely
- Report health and processing status
- Support graceful shutdown
- Handle cancellation
- Avoid processing the same job concurrently
- Maintain structured logs
- Support configurable resource limits

Provide PowerShell scripts for installing, starting, stopping, and uninstalling Windows Services.

Use suitable Windows Service identities and filesystem permissions.

## 7. Project Directory Structure

Create an organized solution similar to:

```text
MovieRecapPlatform/
├── src/
│   ├── BuildingBlocks/
│   ├── Modules/
│   │   ├── Media/
│   │   ├── AI/
│   │   ├── Recap/
│   │   ├── Localization/
│   │   ├── Rights/
│   │   └── Publishing/
│   ├── Api/
│   ├── Dashboard/
│   ├── Workers/
│   │   ├── ImportWorker/
│   │   ├── ProcessingWorker/
│   │   ├── AIWorker/
│   │   ├── RenderingWorker/
│   │   └── PublishingWorker/
│   └── Infrastructure/
├── tests/
│   ├── UnitTests/
│   ├── IntegrationTests/
│   └── ArchitectureTests/
├── docs/
│   ├── architecture/
│   ├── requirements/
│   ├── api/
│   ├── database/
│   ├── deployment/
│   ├── implementation/
│   └── decisions/
├── scripts/
├── infrastructure/
├── .openai/
├── AGENTS.md
└── README.md
```

Adjust the structure if needed, but document all architectural decisions.

## 8. Codex Agent Instructions

Create an `AGENTS.md` file at the repository root and relevant instructions inside `.openai/`.

Apply the following rules to every implementation task:

1. Inspect the existing solution before making changes.
2. Follow Domain-Driven Design and Clean Architecture.
3. Do not introduce unnecessary dependencies.
4. Implement production-quality error handling.
5. Include unit and integration tests for meaningful logic.
6. Never hardcode credentials or API keys.
7. Run builds and relevant tests after changes.
8. Do not claim success when tests fail.
9. Update the appropriate documentation after each implementation.
10. Keep `docs/architecture/` synchronized with architectural changes.
11. Keep `docs/implementation/` synchronized with completed features.
12. Update `README.md` when setup or usage changes.
13. Record important decisions as Architecture Decision Records.
14. Report completed work, changed files, tests, limitations, and next steps.
15. Do not mark a feature complete if it contains only mocks or placeholders.

## 9. Documentation Deliverables

Create and maintain:

- Project Overview
- Product Requirements Document
- Software Architecture Document
- Domain Model
- Database Design
- AI Processing Pipeline
- Video Processing Pipeline
- Localization and Burmese TTS Design
- Publishing Integration Design
- Rights and Compliance Design
- Windows Service Deployment Guide
- Local Development Guide
- Testing Strategy
- Implementation Roadmap
- Architecture Decision Records

Include Mermaid diagrams where helpful.

## 10. Implementation Strategy

Work incrementally.

**Phase 1 — Foundation**

Implement:

- .NET solution
- Domain and application projects
- Infrastructure project
- API project
- Windows Worker Service
- PostgreSQL integration
- Entity Framework Core migrations
- Structured logging
- Health checks
- Basic job lifecycle
- Configuration
- Initial documentation
- Unit tests

**Phase 2 — Media Import and Preprocessing**

Implement:

- Folder watcher
- File stability checks
- Media import
- FFprobe integration
- Media metadata
- Subtitle stream handling
- Scene detection integration

**Phase 3 — AI Recap**

Implement:

- Speech transcription
- Scene analysis
- Structured AI analysis results
- Story summarization
- Recap script generation
- Script editing and approval

**Phase 4 — Burmese Voice and Subtitles**

Implement:

- Burmese localization
- TTS provider integration
- Audio generation
- Subtitle synchronization
- Subtitle export

**Phase 5 — Video Rendering**

Implement:

- Timeline generation
- FFmpeg rendering
- Voice mixing
- Subtitle burning
- Multiple output profiles
- Video validation

**Phase 6 — Dashboard**

Implement:

- Job management
- Movie management
- Recap editor
- Video preview
- Approval controls
- Publishing schedule

**Phase 7 — Publishing**

Implement:

- Official platform API adapters
- Account authorization
- Upload management
- Scheduler
- Publication tracking

**Phase 8 — Production Hardening**

Implement:

- Performance optimization
- Resource controls
- Monitoring
- Security review
- Recovery testing
- Deployment automation

## 11. Acceptance Criteria

The complete platform must be capable of:

1. Importing a supported, authorized movie file.
2. Detecting its metadata, scenes, and audio.
3. Generating a structured recap script.
4. Producing a Burmese-language narration when a verified TTS provider is configured.
5. Generating synchronized Burmese subtitles.
6. Rendering a playable recap video.
7. Supporting human review and approval.
8. Scheduling an approved video for publication.
9. Publishing through supported official platform APIs when the account is eligible and configured.
10. Recovering safely from processing failures.
11. Displaying processing progress and errors.
12. Maintaining accurate project documentation.

External API credentials and paid AI services must be optional for local development. Provide mock implementations and fixtures so the foundation can be tested without them.

## 12. First Execution Instructions

**Do not attempt to implement the entire platform in one execution.**

Start by:

1. Inspecting the working directory and existing files.
2. Creating a concise architecture and implementation plan.
3. Identifying any important technical assumptions.
4. Creating the complete solution structure.
5. Implementing Phase 1 only.
6. Adding the initial database migrations and a working background job lifecycle.
7. Adding unit tests and a local setup guide.
8. Running the build and tests.
9. Updating the project documentation.
10. Reporting what is implemented and what remains.

Do not delete or overwrite existing user files without confirmation.

At the end of Phase 1, provide the exact commands to run the API, database, and Windows worker locally, and recommend the next implementation task.

**Begin Phase 1 now.**