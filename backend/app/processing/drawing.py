"""Composite the colored UI drawing over an image using straight-alpha BGRA8."""

import numpy as np
from app.models.image_types import ImageArray


def apply_drawing(image: ImageArray, mask: ImageArray) -> ImageArray:
    for name, pixels in (("image", image), ("mask", mask)):
        if not isinstance(pixels, np.ndarray):
            raise TypeError(f"{name} must be a NumPy array")
        if pixels.dtype != np.uint8 or pixels.ndim != 3 or pixels.shape[2] != 4:
            raise ValueError(f"{name} must be uint8 BGRA8 (height, width, 4)")
        if pixels.shape[0] == 0 or pixels.shape[1] == 0:
            raise ValueError(f"{name} dimensions must be positive")
    if mask.shape != image.shape:
        raise ValueError("Mask dimensions must match the image")

    result = image.copy(order="C")

    # Bound temporary widened arrays even when the image approaches the IPC limit.
    rows_per_chunk = max(1, 262144 // image.shape[1])
    for top in range(0, image.shape[0], rows_per_chunk):
        rows = slice(top, top + rows_per_chunk)
        covered = mask[rows, :, 3] != 0
        if not np.any(covered):
            continue
        foreground = mask[rows][covered].astype(np.uint32)
        background = image[rows][covered].astype(np.uint32)

        front_weight = foreground[:, 3] * 255
        back_weight = background[:, 3] * (255 - foreground[:, 3])

        alpha = front_weight + back_weight
        colors = (
            foreground[:, :3] * front_weight[:, None]
            + background[:, :3] * back_weight[:, None]
        )
        
        blended = np.empty(foreground.shape, dtype=np.uint8)
        blended[:, :3] = np.floor(colors / alpha[:, None] + 0.5)
        blended[:, 3] = (alpha + 127) // 255
        result[rows][covered] = blended

    return result
