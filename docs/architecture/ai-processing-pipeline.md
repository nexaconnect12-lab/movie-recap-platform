# AI processing pipeline — Phase 3 design, not implemented
Media → chunked ASR + representative source frames → scene analysis → chronological evidence-backed story outline → recap draft → human edits/approval. Provider-agnostic interfaces will separate ASR, visual analysis and narrative generation. Configure providers/models explicitly; never assume speech/language capabilities.

Every plot claim should link source time ranges and evidence, with confidence and provenance. Unsupported events must be rejected or marked uncertain. Chunking bounds memory/token use; summarize with overlap while preserving chronology/characters. Persist prompt/version, provider/model identity, usage/cost, evidence and review state without secrets. Tests need small owned fixtures and deterministic mocks; real-provider acceptance is separate. No provider or mock completion path exists in Phase 1.

