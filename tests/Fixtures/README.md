# Owned media fixtures

These one-second 160x90, 10 fps clips were generated for this repository from FFmpeg `lavfi` testsrc2 and sine filters. They contain no movie footage or third-party recordings. They are solely technical test fixtures, not recap/rendering outputs.

Generation used FFmpeg 9.0.2 (Gyan essentials build). MP4/MOV: MPEG-4 video + AAC audio. MKV: FFV1 + PCM audio. AVI: MPEG-4 + PCM audio. `owned-silent.mp4` has video only. Test inputs are copied into uniquely named temporary folders; originals are never overwritten by tests.

Generate only when the destination does not already exist (`-n`):
```powershell
ffmpeg -hide_banner -loglevel error -n -f lavfi -i 'testsrc2=size=160x90:rate=10:duration=1' -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=1' -c:v mpeg4 -c:a aac -shortest owned-sample.mp4
```
Use `.mov` for MOV, `-c:v ffv1 -c:a pcm_s16le` for MKV, and `-c:v mpeg4 -c:a pcm_s16le` for AVI. Omit the audio input and `-c:a` for the silent clip.

Set `TEST_FFPROBE` to the actual executable to run probe tests. `TEST_POSTGRES` is additionally required for persisted import and separate-host smoke tests. Missing prerequisites skip explicitly and are not verification.

Stream-selection fixtures (generated in the authorized Phase 2 slice, from the same local FFmpeg 9.0.2 bundle):
- `owned-multistream.mkv`: two FFV1 videos (160x90 testsrc2 / 80x60 blue), three PCM sine audio tracks (440/880/220 Hz; language eng/jpn/missing), and one forced English SubRip track from `owned-caption.srt`. Global indices are 0/1 videos, 2/3/4 audio, 5 subtitle. The second audio is stereo. Video 0/audio 2 are default; video 1/audio 3/4 are not. Per-video duration may be absent in FFprobe and stays unknown.
- `owned-cover.png`: synthetic 64x64 yellow image generated from lavfi color, used only as test input.
- `owned-cover.mp4`: existing owned-silent video remuxed with the synthetic image as an attached-picture MJPEG stream. It tests cover-art rejection without any movie/third-party artwork.
- `owned-caption.srt`: one synthetic one-second caption; no movie dialogue.

Generation used `-n` to preserve existing inputs. Multi-stream mapping: `-map 0:v -map 1:v -map 2:a -map 3:a -map 4:a -map 5:s -c:v ffv1 -c:a pcm_s16le -ac:a:1 2 -c:s srt`; language/default/forced metadata is set explicitly. Cover remux uses `-map 0:v -map 1:v -c:v:0 copy -c:v:1 mjpeg -disposition:v:1 attached_pic`. These files are test fixtures, not a delivered stream-extraction/rendering workflow.
