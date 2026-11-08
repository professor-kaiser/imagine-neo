"""Example tint plugin: mix a color while preserving source alpha."""
import numpy as np
from app.plugins import (
    ImagePlugin, validate_bgra, validate_image_selection, apply_masked_result,
)


class TintPlugin(ImagePlugin):
    def __init__(self, color: list[int], amount: float):
        self.color = validate_bgra(color)
        if not 0.0 <= amount <= 1.0:
            raise ValueError("amount must be between 0 and 1")
        self.amount = amount

    def apply(self, image, selection, masks):
        validate_image_selection(image, selection, masks)
        result = image.copy()
        weight = self.amount * self.color[3] / 255.0
        mixed = (
            image[:, :, :3].astype(np.float32) * (1.0 - weight)
            + np.array(self.color[:3], dtype=np.float32) * weight
        )
        result[:, :, :3] = np.clip(np.rint(mixed), 0, 255).astype(np.uint8)
        return apply_masked_result(image, result, selection, masks)
