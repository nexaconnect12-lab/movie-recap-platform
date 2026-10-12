# Owned media fixtures

These one-second 160x90, 10 fps clips were generated for this repository from FFmpeg `lavfi` testsrc2 and sine filters. They contain no movie footage or third-party recordings. They are solely technical test fixtures, not recap/rendering outputs.

Generation used FFmpeg 9.0.2 (Gyan essentials build). MP4/MOV: MPEG-4 video + AAC audio. MKV: FFV1 + PCM audio. AVI: MPEG-4 + PCM audio. `owned-silent.mp4` has video only. Test inputs are copied into uniquely named temporary folders; originals are never overwritten by tests.

Generate only when the destination does not already exist (`-n`):
```powershell
ffmpeg -hide_banner -loglevel error -n -f lavfi -i 'testsrc2=size=160x90:rate=10:duration=1' -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=1' -c:v mpeg4 -c:a aac -shortest owned-sample.mp4
```
Use `.mov` for MOV, `-c:v ffv1 -c:a pcm_s16le` for MKV, and `-c:v mpeg4 -c:a pcm_s16le` for AVI. Omit the audio input and `-c:a` for the silent clip.

Set `TEST_FFPROBE` to the actual executable to run probe tests. `TEST_POSTGRES` is additionally required for persisted import and separate-host smoke tests. Missing prerequisites skip explicitly and are not verification.
