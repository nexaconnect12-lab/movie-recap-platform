# Software architecture
One codebase and shared PostgreSQL instance, two independently hosted processes. Domain and Application have no persistence or host dependencies. Infrastructure implements the application-owned IJobStore. API and Worker are composition roots. No broker or Kubernetes.

```mermaid
flowchart LR
  Client --> API[ASP.NET Core API]
  API --> App[Jobs Application]
  Worker[Windows / Console Worker] --> App
  App --> Domain[Jobs Domain]
  Infra[Infrastructure: EF + PostgreSQL] -.implements IJobStore.-> App
  API --> Infra
  Worker --> Infra
  Infra --> DB[(PostgreSQL jobs schema)]
```

The implemented Jobs module owns ProcessingJobs and AuditLogs. Query results expose immutable views; lease tokens never appear in API responses. Commands require the current version. Internal worker writes require an unexpired matching execution token. A lease fences database updates; it cannot undo future external side effects. Every future executor must use operation-specific idempotency and cancellation.

API requires a random shared key, uses constant-time comparison of key digests, validates commands with FluentValidation and returns problem details. Bind locally for development; role-based user authentication, TLS, quotas and request throttling are production work. Health endpoints return no detailed exception data. Readiness queries the migrated table; liveness is independent of database availability.

Structured logs use Serilog compact JSON. Worker logs optionally rotate by day and size with 14-file retention. Database progress/audit is authoritative for worker status; explicit worker heartbeat dashboard/metrics and log centralization remain later work.

Future boundaries: Media Management owns imports/assets; Video Processing owns probes/scenes; AI Analysis owns transcripts/analysis; Recap owns projects/scripts/segments; Localization owns localized versions; Voice/Subtitles owns speech and text assets; Rendering owns render jobs/outputs; Rights owns licenses/approvals; Publishing owns accounts/attempts/videos; Scheduling owns persistent Quartz triggers; Administration owns settings/read models. See module catalog and ADRs.

Phase 2 manual import adds Media Domain/Application and the module-owned `media` schema. Its Infrastructure adapters inspect trusted local folders and run FFprobe. API enqueues an import; worker inspects and atomically commits media + job completion through the Jobs Application `IJobTransaction` contract. Media persistence never reads Jobs tables directly. A repeatable-read snapshot assembles status through the same Jobs contract. No cross-module EF relationship is added. Rights metadata is import-scoped Media storage for this slice; a separate publication Rights service and scene processing remain future work. See ADR 0005.

Phase 2 folder discovery adds a worker-hosted monitor and Media-owned MediaDiscoveries. Coalesced filesystem hints request scans; startup/periodic reconciliation recovers missed events. Operators use authenticated inbox APIs to dismiss or submit a versioned candidate with explicit source/rights metadata. Submission atomically links the candidate and creates its import/rights/job/audit through existing contracts. File revisions retain history and bind execution to an expected metadata fingerprint. Discovery is opt-in and does not manufacture permissions or import jobs. See ADR 0006.

Phase 2 streams extend Media-owned probing/storage with immutable asset inventories and import-local selections/audits. Selection verifies source identity under a retained read-only handle and saves state/audit together under a row/version lock. Legacy inventories are not guessed: a media.stream-inspection job performs actual hash/probe verification and fenced persistence through Jobs Application contracts. Duplicate assets share inventory but have independent choices. Future scene/processing modules consume Media Application contracts; they do not query its tables. No stream extraction, decoding, scene detection or rendering is delivered. See ADR 0007.


Phase 2 scene detection adds framework-independent VideoProcessing Domain/Application and the `video` schema. Selected-video snapshots/source handles and same-transaction selection validation come from Media Application contracts; their Media-owned adapters alone query Media storage. Scene persistence uses Jobs IJobTransaction, with atomic operation/job creation and fenced analysis/ranges/job/audit completion. Real PySceneDetect/PyAV runs in a bounded shell-free Python subprocess with process-tree cancellation. Sources stay read-only; successful old results remain history after selection changes. See ADR 0008 and ../api/scene-analysis.md. Earlier slice descriptions are historical; extraction/AI/TTS/rendering/publishing remain future.

Phase 2 selected audio extends VideoProcessing with AudioExtraction and application-owned artifact/extractor ports. Media supplies selected-audio/source/transaction contracts; its adapter alone locks/queries its schema. Jobs exposes a locked lease check before promotion and an expiry check on completion. Filesystem rename precedes atomic reference/job/audit commit; only committed references are downloadable. Unique lease files/exclusive attempt locks and operation advisory locks make retries and reference-aware cleanup safe. Real bounded FFmpeg emits actual PCM. See ADR 0009 and ../api/audio-extraction.md. Prior paragraphs describe earlier slice scope; ASR/AI/TTS/rendering/publishing remain future.

Phase 2 frame sampling extends VideoProcessing with local FrameSampling/FrameSample aggregates tied to immutable completed scenes. Media contracts supply selected-video/source and locked binding validation; Jobs contracts fence promotion/completion/audit. FFmpeg decodes selected ordinals and reports integer source PTS/timebase; offset-normalized samples must stay inside stored scene ranges. Shared artifact path/ownership/link guards serve both audio and frames. Unique batch directories, exclusive attempt locks, reference-gated directory promotion and cleanup preserve retries/history. See ADR 0010 and ../api/scene-frames.md. Previous descriptions are historical; no semantic/AI/transcription/rendering/publishing implementation is implied.

Phase 2 preparation adds VideoProcessing-owned reviews/validations/audits. Media's primitive preparation DTO/source/transaction contracts keep all Media table access in its adapter; Video and Jobs coordinate through existing application transactions. Combined metadata view selects current completed bundles with bounded history and a canonical binding hash. Real validation holds verified source/artifact read handles through fenced report commit; fresh approval repeats these checks. Import-level publication locks synchronize scene/audio/frame completions with report/decision acceptance. Exact historical bindings/versions/operator notes are persisted. Effective stale status preserves prior decisions. See ADR 0011 and ../api/preparation-review.md. Technical approval never changes publication rights or runs later-phase processing.
