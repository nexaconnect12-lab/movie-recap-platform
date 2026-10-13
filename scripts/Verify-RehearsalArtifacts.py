"""Independent checks for the owned multi-stream fixture's derived artifacts."""
import json
import sys
import wave
import cv2
import numpy as np

with wave.open(sys.argv[1], "rb") as audio:
    assert (audio.getnchannels(), audio.getsampwidth(), audio.getframerate()) == (1, 2, 16000)
    samples = np.frombuffer(audio.readframes(audio.getnframes()), dtype="<i2")
    assert len(samples) == 16000
    spectrum = np.abs(np.fft.rfft(samples.astype(float)))
    dominant = int(np.argmax(spectrum[1:]) + 1)
    assert abs(dominant - 880) <= 1, dominant
for path in sys.argv[2:]:
    image = cv2.imread(path)
    assert image is not None and image.shape == (180, 320, 3)
    b, g, r = map(int, image[90, 160])
    assert b > 200 and g < 30 and r < 30, (b, g, r)
    assert max(map(int, image[90, 0])) < 10
print(json.dumps({"audioSamples": len(samples), "dominantHz": dominant, "blueFrames": len(sys.argv) - 2}))
