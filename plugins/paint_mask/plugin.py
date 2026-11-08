"""Convert painted coverage to a binary OpenCV mask, independent of preview color."""
import numpy as np
from app.plugins import ImagePlugin, validate_image_selection, encode_mask


class PaintMaskPlugin(ImagePlugin):
    def apply(self, image, selection, masks):
        validate_image_selection(image, selection, masks)
        # Brush coverage is carried by alpha. Black/white is only a preview preference.
        mask = np.where(image[:, :, 3] > 0, 255, 0).astype(np.uint8)
        return encode_mask(mask, selection)
