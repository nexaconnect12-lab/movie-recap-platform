"""Run with the real scene Python environment; tests the acceptance verifier's failure cases."""
import pathlib
import subprocess
import sys
import tempfile
import unittest
import wave

import cv2
import numpy as np

VERIFIER = pathlib.Path(__file__).resolve().parents[2] / "scripts" / "Verify-RehearsalArtifacts.py"


class RehearsalArtifactChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="recap_operational_")
        self.addCleanup(self.temp.cleanup)
        self.wav = pathlib.Path(self.temp.name) / "sample.wav"
        self.png = pathlib.Path(self.temp.name) / "sample.png"
        self.audio(880)
        self.image()

    def audio(self, frequency, rate=16000):
        samples = (np.sin(2 * np.pi * frequency * np.arange(rate) / rate) * 10000).astype("<i2")
        with wave.open(str(self.wav), "wb") as output:
            output.setparams((1, 2, rate, rate, "NONE", "not compressed"))
            output.writeframes(samples.tobytes())

    def image(self, color=(255, 0, 0), padding=True):
        pixels = np.zeros((180, 320, 3), dtype=np.uint8)
        pixels[:, 40:280] = color
        if not padding:
            pixels[:] = color
        self.assertTrue(cv2.imwrite(str(self.png), pixels))

    def verify(self):
        return subprocess.run([sys.executable, str(VERIFIER), str(self.wav), str(self.png)], capture_output=True, text=True, timeout=30)

    def test_expected_audio_and_blue_padded_frame(self):
        result = self.verify()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('"dominantHz": 880', result.stdout)

    def test_default_audio_track_is_rejected(self):
        self.audio(440)
        self.assertNotEqual(0, self.verify().returncode)

    def test_wrong_audio_profile_is_rejected(self):
        self.audio(880, 48000)
        self.assertNotEqual(0, self.verify().returncode)

    def test_other_video_color_is_rejected(self):
        self.image((0, 0, 255))
        self.assertNotEqual(0, self.verify().returncode)

    def test_missing_fit_padding_is_rejected(self):
        self.image(padding=False)
        self.assertNotEqual(0, self.verify().returncode)

    def test_truncated_image_is_rejected(self):
        self.png.write_bytes(self.png.read_bytes()[:24])
        self.assertNotEqual(0, self.verify().returncode)


if __name__ == "__main__":
    unittest.main()
