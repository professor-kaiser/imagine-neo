"""Laplacian contour map with source alpha preserved."""
import cv2
import numpy as np
from app.plugins import ImagePlugin, validate_image_selection, apply_masked_result
from app.models.image_types import ImageArray, Rect


class LaplacianPlugin(ImagePlugin):
    def apply(self, image: ImageArray, selection: Rect | None, masks: list[ImageArray]) -> ImageArray:
        validate_image_selection(image, selection, masks)
        r = selection or Rect(0, 0, image.shape[1], image.shape[0])
        area = (slice(r.y, r.y + r.height), slice(r.x, r.x + r.width))
        gray = cv2.cvtColor(image[area], cv2.COLOR_BGRA2GRAY)
        laplacian = cv2.Laplacian(gray, cv2.CV_64F)
        normalized = cv2.normalize(laplacian, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)
        result = image.copy()
        result[area][:, :, :3] = cv2.cvtColor(normalized, cv2.COLOR_GRAY2BGR)
        return apply_masked_result(image, result, selection, masks)
