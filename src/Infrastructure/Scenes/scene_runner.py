"""Read-only selected-stream decoding and real PySceneDetect. Protocol v1: bounded JSON lines."""
import json
import math
import platform
import sys
from array import array
from fractions import Fraction


class SceneFailure(Exception):
    pass


def emit(event):
    print(json.dumps(event, separators=(",", ":"), allow_nan=False), flush=True)


def run(request):
    import av
    import cv2
    import scenedetect
    from scenedetect import FrameTimecode
    from scenedetect.scene_detector import FlashFilter
    from scenedetect.detectors import ContentDetector

    if scenedetect.__version__ != "0.7.1" or av.__version__ != "19.0.0":
        raise SceneFailure("scene-runtime-version-mismatch")
    cv2.setNumThreads(1)
    detector = ContentDetector(threshold=request["threshold"], min_scene_len=request["minimumSceneFrames"], filter_mode=FlashFilter.Mode.SUPPRESS)
    cuts = []
    timestamps = array("d")
    first_time = None
    last_duration = None
    varied = False
    first_delta = None
    with av.open(request["path"], mode="r", options={"protocol_whitelist": "file,pipe"}) as container:
        selected = next((s for s in container.streams if s.index == request["videoIndex"] and s.type == "video"), None)
        if selected is None or selected.disposition & type(selected.disposition).attached_pic:
            raise SceneFailure("scene-video-stream-unavailable")
        selected.codec_context.thread_count = 1
        fps = float(request["nominalFrameRate"])
        if not math.isfinite(fps) or fps <= 0:
            raise SceneFailure("scene-frame-rate-unavailable")
        for frame in container.decode(selected):
            count = len(timestamps)
            if count >= request["maxFrames"] or frame.width * frame.height > request["maxFramePixels"]:
                raise SceneFailure("scene-resource-limit")
            if frame.pts is None or frame.time_base is None:
                raise SceneFailure("scene-timestamps-unavailable")
            absolute = Fraction(frame.pts) * frame.time_base
            if first_time is None:
                first_time = absolute
            seconds = float(absolute - first_time)
            if not math.isfinite(seconds) or seconds < 0 or timestamps and seconds <= timestamps[-1]:
                raise SceneFailure("scene-nonmonotonic-timestamps")
            if seconds > request["maxVideoSeconds"]:
                raise SceneFailure("scene-resource-limit")
            if timestamps:
                delta = seconds - timestamps[-1]
                if first_delta is None:
                    first_delta = delta
                elif abs(delta - first_delta) > 0.000001:
                    varied = True
            timestamps.append(seconds)
            last_duration = float(frame.duration * frame.time_base) if frame.duration else None
            for cut in detector.process_frame(FrameTimecode(count, fps), frame.to_ndarray(format="bgr24")):
                cuts.append(cut.frame_num)
            if len(cuts) >= request["maxScenes"]:
                raise SceneFailure("scene-resource-limit")
            if count % 100 == 0:
                emit({"event": "progress", "frames": count + 1, "seconds": seconds})
        if not timestamps:
            raise SceneFailure("scene-no-decoded-frames")
        cuts.extend(cut.frame_num for cut in detector.post_process(FrameTimecode(len(timestamps), fps)))
        # Use the final decoded frame's actual duration, or a real selected-stream end timestamp.
        if last_duration is not None and math.isfinite(last_duration) and last_duration > 0:
            end_seconds = timestamps[-1] + last_duration
        elif selected.duration is not None and selected.time_base is not None and selected.start_time is not None:
            end_seconds = float((selected.start_time + selected.duration) * selected.time_base - first_time)
        else:
            raise SceneFailure("scene-final-duration-unavailable")
        if not math.isfinite(end_seconds) or end_seconds <= timestamps[-1] or end_seconds > request["maxVideoSeconds"]:
            raise SceneFailure("scene-invalid-duration")
    if cuts != sorted(set(cuts)) or any(cut <= 0 or cut >= len(timestamps) for cut in cuts):
        raise SceneFailure("scene-invalid-detector-boundary")
    bounds = [0, *cuts, len(timestamps)]
    if len(bounds) - 1 > request["maxScenes"]:
        raise SceneFailure("scene-resource-limit")
    scenes = [{"startFrame": a, "endFrame": b, "startSeconds": timestamps[a], "endSeconds": timestamps[b] if b < len(timestamps) else end_seconds} for a, b in zip(bounds, bounds[1:])]
    emit({"event": "result", "result": {
        "videoIndex": request["videoIndex"], "totalFrames": len(timestamps), "durationSeconds": end_seconds,
        "sourceOffsetSeconds": float(first_time), "variableFrameRate": varied, "timingMode": "presentation-timestamps",
        "sceneDetectVersion": scenedetect.__version__, "pyAvVersion": av.__version__, "openCvVersion": cv2.__version__,
        "pythonVersion": platform.python_version(), "algorithm": "content/suppress", "threshold": request["threshold"],
        "minimumSceneFrames": request["minimumSceneFrames"], "scenes": scenes}})


if __name__ == "__main__":
    try:
        request_line = sys.stdin.readline(16385)
        if len(request_line) > 16384:
            raise SceneFailure("scene-invalid-request")
        run(json.loads(request_line))
    except SceneFailure as error:
        emit({"event": "error", "code": str(error)})
        sys.exit(2)
    except ImportError:
        emit({"event": "error", "code": "scene-runtime-unavailable"})
        sys.exit(2)
    except Exception:
        # No raw paths, user metadata, decoder diagnostics or tracebacks enter the protocol/logs.
        emit({"event": "error", "code": "scene-decode-failed"})
        sys.exit(2)
