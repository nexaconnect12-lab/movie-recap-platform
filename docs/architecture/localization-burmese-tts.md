# Localization and Burmese TTS — Phase 4 design, not implemented
Define extensible language policies for Unicode normalization, sentence splitting, punctuation, terminology and proper names. Burmese-first behavior belongs in a language adapter, not scattered job logic. Preserve original scripts and version localized edits.

TTS provider contract should return audio chunks, voice/language metadata, sample rate, duration and timestamps where available. Verify a real Burmese voice with representative text, pronunciation and quality before claiming support. Use mock speech only for explicit tests, with no production capability claim.

Align final narration via provider timestamps or forced alignment, export SRT/WebVTT/ASS, validate timing, use proper Burmese shaping/font packaging and vertical safe margins. Audio normalization and chunk concatenation must preserve alignment. No TTS provider or language capability was verified in Phase 1.

