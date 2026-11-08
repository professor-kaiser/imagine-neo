"""Generate deterministic, distinct PNG fixtures with OpenCV for actual UI decoding."""

from pathlib import Path
import cv2
import numpy as np


def main() -> None:
    """Write two (96,128,4) uint8 BGRA8 PNGs under artifacts/fixtures; raise OSError on failure."""
    folder = Path(__file__).parents[2] / "artifacts" / "fixtures"
    folder.mkdir(parents=True, exist_ok=True)
    y, x = np.indices((96, 128))
    for index, name in enumerate(("sample-alpha.png", "sample-color.png")):
        pixels = np.empty((96, 128, 4), dtype=np.uint8)
        pixels[:, :, 0] = (x * 2 + index * 60) % 256
        pixels[:, :, 1] = (y * 2 + 30) % 256
        pixels[:, :, 2] = (x + y + index * 100) % 256
        pixels[:, :, 3] = np.where(x < 24, x * 10, 255) if index == 0 else 255
        if not cv2.imwrite(str(folder / name), pixels):
            raise OSError(f"Unable to write {name}")


if __name__ == "__main__":
    main()
