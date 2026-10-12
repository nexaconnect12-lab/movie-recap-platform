# Video processing pipeline — manual hash/probe implemented; further processing is design
Stable authorized input → hash/probe → stream selection/black-bar and overlay analysis → scene detection/thumbnails/audio → edit timeline → FFmpeg render → probe/validate final output. Preserve the original file. Do not process files still being copied. Python/PySceneDetect is a bounded subprocess adapter, not embedded domain logic.

Invoke tools without shell interpolation, constrain paths/resources, stream progress, terminate process trees on cancellation, retain stderr safely and record tool/version provenance. Removable selectable subtitles and authorized overlay edits are distinct from ownership-watermark concealment, which is prohibited.

Rendering uses configurable landscape 1920×1080 and vertical 1080×1920 profiles, narration/audio mix, aligned subtitles, cleanup and hardware encoding capability checks. Validate duration, streams and playability before promoting temporary files atomically. Phase 1 has no FFmpeg, probe, scene detection or render execution.

Phase 2 manual import now implements stable-file observation, SHA-256 deduplication and real FFprobe metadata/container/video-stream validation through Media Application ports. It accepts rights assertions for inspection while marking them Unreviewed. No stream extraction/selection workflow, black-bar/overlay analysis, scene detection, thumbnails, audio extraction or rendering is implemented. FFmpeg was used solely to generate owned synthetic test inputs. Production subprocess stderr is bounded and discarded rather than persisted because arbitrary media/tool diagnostics may contain sensitive metadata. See ADR 0005 and the manual-import API guide.

Optional worker folder monitoring now feeds a persistent discovery inbox. Versioned source/rights submission creates an import bound to that observed location/file revision, using the same real hash/probe pipeline. Changed sources require a new discovery submission; monitoring alone creates no jobs. See ADR 0006.

